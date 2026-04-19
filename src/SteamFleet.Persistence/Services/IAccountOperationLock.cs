using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SteamFleet.Persistence.Services;

public interface IAccountOperationLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryAccountOperationLock : IAccountOperationLock
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static readonly AsyncLocal<HashSet<Guid>?> AmbientLocks = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (AmbientLocks.Value?.Contains(accountId) == true)
        {
            return new NoopLease();
        }

        var semaphore = Locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        var held = AmbientLocks.Value ?? [];
        held.Add(accountId);
        AmbientLocks.Value = held;

        return new Lease(accountId, semaphore);
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease(Guid accountId, SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            var held = AmbientLocks.Value;
            held?.Remove(accountId);
            if (held is { Count: 0 })
            {
                AmbientLocks.Value = null;
            }

            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class AccountOperationLock(
    IConfiguration configuration,
    ILogger<AccountOperationLock> logger) : IAccountOperationLock
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> InMemoryLocks = new();
    private static readonly AsyncLocal<HashSet<Guid>?> AmbientLocks = new();

    private readonly string? _connectionString = configuration.GetConnectionString("Postgres")
                                                ?? configuration["POSTGRES_CONNECTION"];

    public async Task<IAsyncDisposable> AcquireAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (IsReentrant(accountId))
        {
            return new NoopLease();
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            var semaphore = InMemoryLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            EnterAmbient(accountId);
            return new InMemoryLease(accountId, semaphore);
        }

        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", connection);
            command.Parameters.AddWithValue("k", ToLockKey(accountId));
            await command.ExecuteNonQueryAsync(cancellationToken);
            EnterAmbient(accountId);
            return new PostgresLease(accountId, connection, logger);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static bool IsReentrant(Guid accountId)
    {
        return AmbientLocks.Value?.Contains(accountId) == true;
    }

    private static void EnterAmbient(Guid accountId)
    {
        var held = AmbientLocks.Value;
        if (held is null)
        {
            held = new HashSet<Guid>();
            AmbientLocks.Value = held;
        }

        held.Add(accountId);
    }

    private static void ExitAmbient(Guid accountId)
    {
        var held = AmbientLocks.Value;
        if (held is null)
        {
            return;
        }

        held.Remove(accountId);
        if (held.Count == 0)
        {
            AmbientLocks.Value = null;
        }
    }

    private static long ToLockKey(Guid accountId)
    {
        Span<byte> bytes = stackalloc byte[16];
        accountId.TryWriteBytes(bytes);
        var left = BitConverter.ToInt64(bytes);
        var right = BitConverter.ToInt64(bytes[8..]);
        return left ^ right;
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryLease(Guid accountId, SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            ExitAmbient(accountId);
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PostgresLease(
        Guid accountId,
        DbConnection connection,
        ILogger<AccountOperationLock> logger) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await using var unlock = connection.CreateCommand();
                unlock.CommandText = "SELECT pg_advisory_unlock(@k)";
                var parameter = unlock.CreateParameter();
                parameter.ParameterName = "k";
                parameter.Value = ToLockKey(accountId);
                unlock.Parameters.Add(parameter);
                await unlock.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release PostgreSQL advisory lock for account {AccountId}", accountId);
            }
            finally
            {
                ExitAmbient(accountId);
                await connection.DisposeAsync();
            }
        }
    }
}
