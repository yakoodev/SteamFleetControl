using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SteamFleet.Persistence.Security;
using SteamFleet.Persistence.Services;

namespace SteamFleet.Persistence.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSteamFleetPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? configuration["POSTGRES_CONNECTION"]
                               ?? throw new InvalidOperationException("Postgres connection string is missing.");

        services.AddDbContext<SteamFleetDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(SteamFleetDbContext).Assembly.FullName);
                npgsql.EnableRetryOnFailure(5);
            });
        });

        var masterKey = configuration["SECRETS_MASTER_KEY_B64"];
        services.AddSingleton<ISecretCryptoService>(_ => new AesGcmSecretCryptoService(masterKey ?? string.Empty));

        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAccountRiskPolicyService, AccountRiskPolicyService>();
        services.AddSingleton<IAccountOperationLock, AccountOperationLock>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IJobService, JobService>();
        services.AddScoped<HangfireJobExecutor>();

        return services;
    }

    public static async Task EnsureSteamFleetDatabaseAsync(this IServiceProvider provider, CancellationToken cancellationToken = default)
    {
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SteamFleetDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var bootstrap = scope.ServiceProvider.GetService<IAdminBootstrapService>();
        if (bootstrap is not null)
        {
            await bootstrap.EnsureInitializedAsync(cancellationToken);
        }
    }
}
