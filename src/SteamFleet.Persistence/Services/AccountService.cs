using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Security;

namespace SteamFleet.Persistence.Services;

public sealed class AccountService(
    SteamFleetDbContext dbContext,
    ISteamAccountGateway steamGateway,
    ISecretCryptoService cryptoService,
    IAuditService auditService) : IAccountService
{
    private const string PasswordPendingPrefix = "password.change.pending.";
    private const string PasswordPendingRequestIdKey = PasswordPendingPrefix + "requestId";
    private const string PasswordPendingContextKey = PasswordPendingPrefix + "confirmationContext";
    private const string PasswordPendingCurrentPasswordKey = PasswordPendingPrefix + "currentPasswordEnc";
    private const string PasswordPendingNewPasswordKey = PasswordPendingPrefix + "newPasswordEnc";
    private const string PasswordPendingDeauthorizeKey = PasswordPendingPrefix + "deauthorizeAfterChange";
    private const string PasswordPendingGeneratedKey = PasswordPendingPrefix + "generated";
    private const string PasswordPendingExpiresAtKey = PasswordPendingPrefix + "expiresAt";
    private static readonly TimeSpan PasswordPendingTtl = TimeSpan.FromMinutes(30);
    private readonly record struct PendingPasswordChangeState(
        string RequestId,
        string? ConfirmationContext,
        string EncryptedCurrentPassword,
        string EncryptedNewPassword,
        bool DeauthorizeAfterChange,
        bool Generated,
        DateTimeOffset ExpiresAt);

    public async Task<AccountsPageResult> GetAsync(AccountFilterRequest request, CancellationToken cancellationToken = default)
    {
        var query = dbContext.SteamAccounts
            .AsNoTracking()
            .Include(x => x.Folder)
            .Include(x => x.ParentAccount)
            .Include(x => x.ChildAccounts)
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.LoginName.ToLower().Contains(q) ||
                (x.DisplayName != null && x.DisplayName.ToLower().Contains(q)) ||
                (x.Email != null && x.Email.ToLower().Contains(q)) ||
                (x.SteamId64 != null && x.SteamId64.Contains(q)));
        }

        if (request.Status is not null)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        if (request.FolderId is not null)
        {
            query = query.Where(x => x.FolderId == request.FolderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            var tag = request.Tag.Trim().ToLowerInvariant();
            query = query.Where(x => x.TagLinks.Any(t => t.Tag != null && t.Tag.Name.ToLower() == tag));
        }

        if (!string.IsNullOrWhiteSpace(request.FamilyGroup))
        {
            var familyGroup = request.FamilyGroup.Trim();
            if (familyGroup.Equals("ungrouped", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => x.ParentAccountId == null && !x.ChildAccounts.Any());
            }
            else if (familyGroup.Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => x.ParentAccountId == null && x.ChildAccounts.Any());
            }
            else if (Guid.TryParse(familyGroup, out var mainAccountId))
            {
                query = query.Where(x => x.Id == mainAccountId || x.ParentAccountId == mainAccountId);
            }
        }

        var total = await query.CountAsync(cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var skip = (page - 1) * pageSize;

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var familyCounts = await BuildFamilyCountsAsync(items.Select(x => x.Id).ToArray(), cancellationToken);

        return new AccountsPageResult
        {
            TotalCount = total,
            Items = items.Select(x => MapAccountDto(x, familyCounts.GetValueOrDefault(x.ParentAccountId ?? x.Id, 1))).ToArray()
        };
    }

    public async Task<AccountDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SteamAccounts
            .AsNoTracking()
            .Include(x => x.Folder)
            .Include(x => x.ParentAccount)
            .Include(x => x.ChildAccounts)
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var familyCounts = await BuildFamilyCountsAsync([entity.Id], cancellationToken);
        return MapAccountDto(entity, familyCounts.GetValueOrDefault(entity.ParentAccountId ?? entity.Id, 1));
    }

    public async Task<AccountDto> CreateAsync(AccountUpsertRequest request, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.SteamAccounts
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.LoginName == request.LoginName, cancellationToken);

        if (existing is not null)
        {
            throw new InvalidOperationException($"Account with login '{request.LoginName}' already exists.");
        }

        var entity = new SteamAccount
        {
            LoginName = request.LoginName,
            CreatedBy = actorId,
            UpdatedBy = actorId
        };

        await ApplyUpsertAsync(entity, request, actorId, cancellationToken);
        await dbContext.SteamAccounts.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountCreated,
            "steam_account",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["login"] = entity.LoginName,
                ["status"] = entity.Status.ToString()
            },
            cancellationToken);

        return await GetByIdAsync(entity.Id, cancellationToken)
               ?? throw new InvalidOperationException("Failed to load created account.");
    }

    public async Task<AccountDto?> UpdateAsync(Guid id, AccountUpsertRequest request, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SteamAccounts
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        await ApplyUpsertAsync(entity, request, actorId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["login"] = entity.LoginName },
            cancellationToken);

        return await GetByIdAsync(entity.Id, cancellationToken);
    }

    public async Task<bool> ArchiveAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SteamAccounts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.Status = AccountStatus.Archived;
        entity.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountArchived,
            "steam_account",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["login"] = entity.LoginName },
            cancellationToken);

        return true;
    }

    public async Task<AccountImportResult> ImportAsync(Stream stream, string fileName, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var requests = await ParseImportAsync(stream, fileName, cancellationToken);
        var result = new AccountImportResult { Total = requests.Count };

        foreach (var request in requests)
        {
            try
            {
                var existing = await dbContext.SteamAccounts
                    .Include(x => x.TagLinks)
                    .ThenInclude(x => x.Tag)
                    .Include(x => x.Secret)
                    .FirstOrDefaultAsync(x => x.LoginName == request.LoginName, cancellationToken);

                if (existing is null)
                {
                    var newAccount = new SteamAccount
                    {
                        LoginName = request.LoginName,
                        CreatedBy = actorId,
                        UpdatedBy = actorId
                    };

                    await ApplyUpsertAsync(newAccount, request, actorId, cancellationToken);
                    await dbContext.SteamAccounts.AddAsync(newAccount, cancellationToken);
                    result.Created++;
                }
                else
                {
                    await ApplyUpsertAsync(existing, request, actorId, cancellationToken);
                    result.Updated++;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{request.LoginName}: {ex.Message}");
            }
        }

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account_import",
            Guid.NewGuid().ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["total"] = result.Total.ToString(CultureInfo.InvariantCulture),
                ["created"] = result.Created.ToString(CultureInfo.InvariantCulture),
                ["updated"] = result.Updated.ToString(CultureInfo.InvariantCulture),
                ["errors"] = result.Errors.Count.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        return result;
    }

    public async Task<byte[]> ExportCsvAsync(AccountFilterRequest filter, CancellationToken cancellationToken = default)
    {
        var page = await GetAsync(new AccountFilterRequest
        {
            Query = filter.Query,
            Status = filter.Status,
            Tag = filter.Tag,
            FamilyGroup = filter.FamilyGroup,
            FolderId = filter.FolderId,
            Page = 1,
            PageSize = 10_000
        }, cancellationToken);

        await using var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteField("id");
        csv.WriteField("login");
        csv.WriteField("displayName");
        csv.WriteField("steamId64");
        csv.WriteField("profileUrl");
        csv.WriteField("email");
        csv.WriteField("folder");
        csv.WriteField("parentLogin");
        csv.WriteField("gamesCount");
        csv.WriteField("status");
        csv.WriteField("tags");
        csv.WriteField("lastCheckAt");
        csv.NextRecord();

        foreach (var item in page.Items)
        {
            csv.WriteField(item.Id);
            csv.WriteField(item.LoginName);
            csv.WriteField(item.DisplayName);
            csv.WriteField(item.SteamId64);
            csv.WriteField(item.ProfileUrl);
            csv.WriteField(item.Email);
            csv.WriteField(item.FolderName);
            csv.WriteField(item.ParentLoginName);
            csv.WriteField(item.GamesCount);
            csv.WriteField(item.Status);
            csv.WriteField(string.Join('|', item.Tags));
            csv.WriteField(item.LastCheckAt?.ToString("O"));
            csv.NextRecord();
        }

        await writer.FlushAsync(cancellationToken);
        return ms.ToArray();
    }

    public async Task<SteamAuthResult> AuthenticateAsync(
        Guid id,
        AccountAuthenticateRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        var password = !string.IsNullOrWhiteSpace(request.Password)
            ? request.Password
            : await ReadSecretAsync(account, account.Secret.EncryptedPassword, "password", actorId, ip, cancellationToken);

        if (string.IsNullOrWhiteSpace(password))
        {
            return new SteamAuthResult
            {
                Success = false,
                ErrorMessage = "Password is required for authentication."
            };
        }

        var sharedSecret = !string.IsNullOrWhiteSpace(request.SharedSecret)
            ? request.SharedSecret
            : await ReadSecretAsync(account, account.Secret.EncryptedSharedSecret, "shared_secret", actorId, ip, cancellationToken);

        var identitySecret = !string.IsNullOrWhiteSpace(request.IdentitySecret)
            ? request.IdentitySecret
            : await ReadSecretAsync(account, account.Secret.EncryptedIdentitySecret, "identity_secret", actorId, ip, cancellationToken);

        var guardData = await ReadSecretAsync(account, account.Secret.EncryptedRecoveryPayload, "guard_data", actorId, ip, cancellationToken);

        var authResult = await steamGateway.AuthenticateAsync(new SteamCredentials
        {
            LoginName = account.LoginName,
            Password = password!,
            SharedSecret = sharedSecret,
            IdentitySecret = identitySecret,
            GuardCode = request.GuardCode,
            GuardData = guardData,
            AllowDeviceConfirmation = request.AllowDeviceConfirmation
        }, cancellationToken);

        account.LastCheckAt = DateTimeOffset.UtcNow;

        if (authResult.Success && !string.IsNullOrWhiteSpace(authResult.Session.CookiePayload))
        {
            await ApplyAuthResultAsync(
                account,
                authResult,
                password,
                sharedSecret,
                identitySecret,
                actorId,
                ip,
                cancellationToken);
        }
        else
        {
            account.Status = AccountStatus.RequiresRelogin;
            account.LastErrorAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "authenticate",
                ["success"] = authResult.Success.ToString()
            },
            cancellationToken);

        return authResult;
    }

    public async Task<SteamQrAuthStartResult> StartQrAuthenticationAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.SteamAccounts.AnyAsync(x => x.Id == id, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException("Account not found.");
        }

        var start = await steamGateway.StartQrAuthenticationAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "qr_auth_start",
                ["flowId"] = start.FlowId.ToString()
            },
            cancellationToken);

        return start;
    }

    public async Task<SteamQrAuthPollResult> PollQrAuthenticationAsync(
        Guid id,
        Guid flowId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var poll = await steamGateway.PollQrAuthenticationAsync(flowId, cancellationToken);
        if (poll.Status == SteamQrAuthStatus.Completed &&
            poll.AuthResult is { Success: true } authResult &&
            !string.IsNullOrWhiteSpace(authResult.Session.CookiePayload))
        {
            await ApplyAuthResultAsync(
                account,
                authResult,
                plaintextPassword: null,
                plaintextSharedSecret: null,
                plaintextIdentitySecret: null,
                actorId,
                ip,
                cancellationToken);
        }
        else if (poll.Status is SteamQrAuthStatus.Failed or SteamQrAuthStatus.Expired)
        {
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.RequiresRelogin;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "qr_auth_poll",
                ["flowId"] = flowId.ToString(),
                ["status"] = poll.Status.ToString()
            },
            cancellationToken);

        return poll;
    }

    public async Task CancelQrAuthenticationAsync(
        Guid id,
        Guid flowId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.SteamAccounts.AnyAsync(x => x.Id == id, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException("Account not found.");
        }

        await steamGateway.CancelQrAuthenticationAsync(flowId, cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "qr_auth_cancel",
                ["flowId"] = flowId.ToString()
            },
            cancellationToken);
    }

    public async Task<SteamSessionValidationResult> ValidateSessionAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var sessionPayload = await GetSessionPayloadAsync(account, actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(sessionPayload))
        {
            return new SteamSessionValidationResult { IsValid = false, Reason = "No session payload configured" };
        }

        var result = await steamGateway.ValidateSessionAsync(sessionPayload, cancellationToken);
        account.LastCheckAt = DateTimeOffset.UtcNow;
        if (result.IsValid)
        {
            account.LastSuccessAt = DateTimeOffset.UtcNow;
            if (account.Status is AccountStatus.RequiresRelogin or AccountStatus.Error)
            {
                account.Status = AccountStatus.Active;
            }
        }
        else
        {
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.RequiresRelogin;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<SteamSessionInfo> RefreshSessionAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var sessionPayload = await GetSessionPayloadAsync(account, actorId, ip, cancellationToken)
                             ?? throw new InvalidOperationException("No session payload configured for refresh.");

        SteamSessionInfo refreshed;
        try
        {
            refreshed = await steamGateway.RefreshSessionAsync(sessionPayload, cancellationToken);
        }
        catch
        {
            var password = await ReadSecretAsync(account, account.Secret?.EncryptedPassword, "password", actorId, ip, cancellationToken)
                           ?? throw new InvalidOperationException("Session refresh failed and password is not configured.");
            var sharedSecret = await ReadSecretAsync(account, account.Secret?.EncryptedSharedSecret, "shared_secret", actorId, ip, cancellationToken);
            var identitySecret = await ReadSecretAsync(account, account.Secret?.EncryptedIdentitySecret, "identity_secret", actorId, ip, cancellationToken);
            var guardData = await ReadSecretAsync(account, account.Secret?.EncryptedRecoveryPayload, "guard_data", actorId, ip, cancellationToken);

            var authResult = await steamGateway.AuthenticateAsync(new SteamCredentials
            {
                LoginName = account.LoginName,
                Password = password,
                SharedSecret = sharedSecret,
                IdentitySecret = identitySecret,
                GuardData = guardData,
                AllowDeviceConfirmation = true
            }, cancellationToken);

            if (!authResult.Success || string.IsNullOrWhiteSpace(authResult.Session.CookiePayload))
            {
                throw new InvalidOperationException($"Session refresh and re-auth both failed: {authResult.ErrorMessage}");
            }

            refreshed = authResult.Session;
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(refreshed.CookiePayload ?? sessionPayload);
        if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
        {
            // refresh token lives inside cookie payload bundle; keeping branch explicit for future extension.
        }
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.LastCheckAt = DateTimeOffset.UtcNow;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.Status = AccountStatus.Active;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["operation"] = "refresh_session" },
            cancellationToken);

        return refreshed;
    }

    public async Task<AccountPasswordChangeResult> ChangePasswordAsync(
        Guid id,
        AccountPasswordChangeRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        var isConfirmationAttempt =
            !string.IsNullOrWhiteSpace(request.ConfirmationCode) ||
            !string.IsNullOrWhiteSpace(request.ConfirmationRequestId);

        if (isConfirmationAttempt)
        {
            if (!TryReadPendingPasswordChange(metadata, out var pending))
            {
                return new AccountPasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = "Нет активного запроса на подтверждение смены пароля. Запустите смену пароля заново.",
                    ReasonCode = SteamReasonCodes.EndpointRejected,
                    Retryable = false
                };
            }

            if (!string.IsNullOrWhiteSpace(request.ConfirmationRequestId) &&
                !string.Equals(request.ConfirmationRequestId, pending.RequestId, StringComparison.Ordinal))
            {
                return new AccountPasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = "Код подтверждения относится к другой операции. Повторите запуск смены пароля.",
                    ReasonCode = SteamReasonCodes.EndpointRejected,
                    Retryable = false
                };
            }

            if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                ClearPendingPasswordChange(metadata);
                account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new AccountPasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = "Окно подтверждения истекло. Запустите смену пароля заново.",
                    ReasonCode = SteamReasonCodes.Timeout,
                    Retryable = false
                };
            }

            if (string.IsNullOrWhiteSpace(request.ConfirmationCode))
            {
                return new AccountPasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = "Введите код подтверждения из письма Steam.",
                    ReasonCode = SteamReasonCodes.GuardPending,
                    Retryable = true,
                    RequiresConfirmation = true,
                    ConfirmationRequestId = pending.RequestId,
                    ConfirmationExpiresAt = pending.ExpiresAt
                };
            }

            string pendingCurrentPassword;
            string pendingNextPassword;
            try
            {
                pendingCurrentPassword = cryptoService.Decrypt(pending.EncryptedCurrentPassword)
                    ?? throw new InvalidOperationException("Pending current password is empty.");
                pendingNextPassword = cryptoService.Decrypt(pending.EncryptedNewPassword)
                    ?? throw new InvalidOperationException("Pending new password is empty.");
            }
            catch (Exception)
            {
                ClearPendingPasswordChange(metadata);
                account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new AccountPasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = "Сессия подтверждения повреждена. Запустите смену пароля заново.",
                    ReasonCode = SteamReasonCodes.EndpointRejected,
                    Retryable = false
                };
            }

            var pendingSessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, pendingCurrentPassword, cancellationToken);
            var pendingChangeResult = await steamGateway.ChangePasswordAsync(
                pendingSessionPayload,
                pendingCurrentPassword,
                pendingNextPassword,
                request.ConfirmationCode.Trim(),
                pending.ConfirmationContext,
                cancellationToken);
            if (!pendingChangeResult.Success)
            {
                if (TryGetConfirmationContext(pendingChangeResult, out var updatedContext) &&
                    !string.IsNullOrWhiteSpace(updatedContext))
                {
                    pending = pending with { ConfirmationContext = updatedContext };
                    SetPendingPasswordChange(metadata, pending);
                }

                account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
                if (string.Equals(pendingChangeResult.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
                {
                    account.UpdatedBy = actorId;
                    account.LastCheckAt = DateTimeOffset.UtcNow;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return new AccountPasswordChangeResult
                    {
                        Success = false,
                        ErrorMessage = pendingChangeResult.ErrorMessage,
                        ReasonCode = pendingChangeResult.ReasonCode,
                        Retryable = pendingChangeResult.Retryable,
                        RequiresConfirmation = true,
                        ConfirmationRequestId = pending.RequestId,
                        ConfirmationExpiresAt = pending.ExpiresAt
                    };
                }

                ApplyGatewayFailureState(account, pendingChangeResult.ReasonCode, actorId);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new AccountPasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = pendingChangeResult.ErrorMessage,
                    ReasonCode = pendingChangeResult.ReasonCode,
                    Retryable = pendingChangeResult.Retryable
                };
            }

            return await FinalizePasswordChangeAsync(
                account,
                metadata,
                pendingNextPassword,
                pending.DeauthorizeAfterChange,
                pending.Generated,
                actorId,
                ip,
                pendingSessionPayload,
                cancellationToken);
        }

        var currentPassword = !string.IsNullOrWhiteSpace(request.CurrentPassword)
            ? request.CurrentPassword
            : await ReadSecretAsync(account, account.Secret.EncryptedPassword, "password", actorId, ip, cancellationToken);

        if (string.IsNullOrWhiteSpace(currentPassword))
        {
            return new AccountPasswordChangeResult
            {
                Success = false,
                ErrorMessage = "Current password is not configured.",
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = false
            };
        }

        var nextPassword = request.NewPassword;
        if (string.IsNullOrWhiteSpace(nextPassword) && request.GenerateIfEmpty)
        {
            nextPassword = GenerateStrongPassword();
        }

        if (string.IsNullOrWhiteSpace(nextPassword))
        {
            return new AccountPasswordChangeResult
            {
                Success = false,
                ErrorMessage = "New password is missing.",
                ReasonCode = SteamReasonCodes.EndpointRejected,
                Retryable = false
            };
        }

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword, cancellationToken);
        var changeResult = await steamGateway.ChangePasswordAsync(
            sessionPayload,
            currentPassword,
            nextPassword,
            cancellationToken: cancellationToken);
        if (!changeResult.Success)
        {
            if (string.Equals(changeResult.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
            {
                var pending = new PendingPasswordChangeState(
                    RequestId: Guid.NewGuid().ToString("N"),
                    ConfirmationContext: TryGetConfirmationContext(changeResult, out var context) ? context : null,
                    EncryptedCurrentPassword: cryptoService.Encrypt(currentPassword),
                    EncryptedNewPassword: cryptoService.Encrypt(nextPassword),
                    DeauthorizeAfterChange: request.DeauthorizeAfterChange,
                    Generated: string.IsNullOrWhiteSpace(request.NewPassword),
                    ExpiresAt: DateTimeOffset.UtcNow.Add(PasswordPendingTtl));

                SetPendingPasswordChange(metadata, pending);
                account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
                account.UpdatedBy = actorId;
                account.LastCheckAt = DateTimeOffset.UtcNow;
                if (account.Status is AccountStatus.Error)
                {
                    account.Status = AccountStatus.Active;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return new AccountPasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = changeResult.ErrorMessage,
                    ReasonCode = changeResult.ReasonCode,
                    Retryable = changeResult.Retryable,
                    RequiresConfirmation = true,
                    ConfirmationRequestId = pending.RequestId,
                    ConfirmationExpiresAt = pending.ExpiresAt
                };
            }

            ClearPendingPasswordChange(metadata);
            account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
            ApplyGatewayFailureState(account, changeResult.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AccountPasswordChangeResult
            {
                Success = false,
                ErrorMessage = changeResult.ErrorMessage,
                ReasonCode = changeResult.ReasonCode,
                Retryable = changeResult.Retryable
            };
        }

        return await FinalizePasswordChangeAsync(
            account,
            metadata,
            nextPassword,
            request.DeauthorizeAfterChange,
            string.IsNullOrWhiteSpace(request.NewPassword),
            actorId,
            ip,
            sessionPayload,
            cancellationToken);
    }

    public async Task<SteamOperationResult> DeauthorizeAllSessionsAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
        var result = await steamGateway.DeauthorizeAllSessionsAsync(sessionPayload, cancellationToken);
        if (!result.Success)
        {
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.Error;
            await dbContext.SaveChangesAsync(cancellationToken);
            return result;
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedSessionPayload = null;
        account.Status = AccountStatus.RequiresRelogin;
        account.UpdatedBy = actorId;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SessionsDeauthorized,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            cancellationToken: cancellationToken);

        return result;
    }

    private async Task<AccountPasswordChangeResult> FinalizePasswordChangeAsync(
        SteamAccount account,
        Dictionary<string, string> metadata,
        string nextPassword,
        bool deauthorizeAfterChange,
        bool generated,
        string actorId,
        string? ip,
        string sessionPayload,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedPassword = cryptoService.Encrypt(nextPassword);
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.UpdatedBy = actorId;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;

        ClearPendingPasswordChange(metadata);
        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);

        var deauthorized = false;
        string? deauthorizeWarning = null;
        string? deauthorizeReasonCode = null;
        var deauthorizeRetryable = false;
        if (deauthorizeAfterChange)
        {
            var deauth = await steamGateway.DeauthorizeAllSessionsAsync(sessionPayload, cancellationToken);
            if (deauth.Success)
            {
                account.Secret.EncryptedSessionPayload = null;
                account.Status = AccountStatus.RequiresRelogin;
                deauthorized = true;
            }
            else
            {
                account.LastErrorAt = DateTimeOffset.UtcNow;
                account.Status = AccountStatus.Active;
                deauthorizeReasonCode = deauth.ReasonCode;
                deauthorizeRetryable = deauth.Retryable;
                deauthorizeWarning = string.IsNullOrWhiteSpace(deauth.ErrorMessage)
                    ? "Пароль изменён, но Steam не подтвердил завершение сессий."
                    : $"Пароль изменён, но завершить сессии не удалось: {deauth.ErrorMessage}";
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.PasswordChanged,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["deauthorized"] = deauthorized.ToString(),
                ["generated"] = generated.ToString(),
                ["deauthorizeWarning"] = (!string.IsNullOrWhiteSpace(deauthorizeWarning)).ToString()
            },
            cancellationToken);

        return new AccountPasswordChangeResult
        {
            Success = true,
            NewPassword = nextPassword,
            Deauthorized = deauthorized,
            ErrorMessage = deauthorizeWarning,
            ReasonCode = deauthorizeReasonCode,
            Retryable = deauthorizeRetryable
        };
    }

    public async Task<AccountGamesPageResult> RefreshGamesAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .Include(x => x.Games)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        SteamOwnedGamesSnapshot snapshot;
        try
        {
            string sessionPayload;
            try
            {
                sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                throw new SteamGatewayOperationException(
                    ex.Message,
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true,
                    ex);
            }

            snapshot = await steamGateway.GetOwnedGamesSnapshotAsync(sessionPayload, cancellationToken);
        }
        catch (SteamGatewayOperationException ex)
        {
            ApplyGatewayFailureState(account, ex.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        var existing = account.Games.ToDictionary(x => x.AppId);
        var incomingAppIds = new HashSet<int>();

        foreach (var game in snapshot.Games)
        {
            incomingAppIds.Add(game.AppId);
            if (existing.TryGetValue(game.AppId, out var row))
            {
                row.Name = game.Name;
                row.PlaytimeMinutes = game.PlaytimeMinutes;
                row.ImgIconUrl = game.ImgIconUrl;
                row.LastSyncedAt = snapshot.SyncedAt;
            }
            else
            {
                account.Games.Add(new SteamAccountGame
                {
                    AccountId = account.Id,
                    AppId = game.AppId,
                    Name = game.Name,
                    PlaytimeMinutes = game.PlaytimeMinutes,
                    ImgIconUrl = game.ImgIconUrl,
                    LastSyncedAt = snapshot.SyncedAt
                });
            }
        }

        var stale = account.Games.Where(x => !incomingAppIds.Contains(x.AppId)).ToList();
        if (stale.Count > 0)
        {
            dbContext.SteamAccountGames.RemoveRange(stale);
        }

        await PersistGamesSnapshotAsync(account, snapshot, actorId, cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.GamesRefreshed,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["games"] = snapshot.Games.Count.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        return await GetGamesAsync(account.Id, AccountGamesScope.Owned, null, 1, Math.Max(snapshot.Games.Count, 1), cancellationToken);
    }

    private async Task PersistGamesSnapshotAsync(
        SteamAccount account,
        SteamOwnedGamesSnapshot snapshot,
        string actorId,
        CancellationToken cancellationToken)
    {
        account.ProfileUrl = snapshot.ProfileUrl;
        account.GamesLastSyncAt = snapshot.SyncedAt;
        account.GamesCount = snapshot.Games.Count;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();

            var fresh = await dbContext.SteamAccounts
                .Include(x => x.Games)
                .FirstOrDefaultAsync(x => x.Id == account.Id, cancellationToken);
            if (fresh is null)
            {
                throw;
            }

            if (fresh.Games.Count > 0)
            {
                dbContext.SteamAccountGames.RemoveRange(fresh.Games);
            }

            foreach (var game in snapshot.Games)
            {
                dbContext.SteamAccountGames.Add(new SteamAccountGame
                {
                    AccountId = fresh.Id,
                    AppId = game.AppId,
                    Name = game.Name,
                    PlaytimeMinutes = game.PlaytimeMinutes,
                    ImgIconUrl = game.ImgIconUrl,
                    LastSyncedAt = snapshot.SyncedAt
                });
            }

            fresh.ProfileUrl = snapshot.ProfileUrl;
            fresh.GamesLastSyncAt = snapshot.SyncedAt;
            fresh.GamesCount = snapshot.Games.Count;
            fresh.LastSuccessAt = DateTimeOffset.UtcNow;
            fresh.LastErrorAt = null;
            fresh.Status = AccountStatus.Active;
            fresh.UpdatedBy = actorId;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<AccountGamesPageResult> GetGamesAsync(
        Guid id,
        AccountGamesScope scope,
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var rootId = account.ParentAccountId ?? account.Id;
        var familyAccounts = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => x.Id == rootId || x.ParentAccountId == rootId)
            .Select(x => new { x.Id, x.LoginName })
            .ToListAsync(cancellationToken);

        if (familyAccounts.Count == 0)
        {
            familyAccounts.Add(new { account.Id, account.LoginName });
        }

        var sourceAccountIds = scope switch
        {
            AccountGamesScope.Owned => new[] { account.Id },
            AccountGamesScope.Family => familyAccounts.Where(x => x.Id != account.Id).Select(x => x.Id).ToArray(),
            _ => familyAccounts.Select(x => x.Id).ToArray()
        };

        if (sourceAccountIds.Length == 0)
        {
            return new AccountGamesPageResult
            {
                Items = [],
                TotalCount = 0,
                Page = Math.Max(1, page),
                PageSize = Math.Clamp(pageSize, 1, 200)
            };
        }

        var gameQuery = dbContext.SteamAccountGames
            .AsNoTracking()
            .Where(x => sourceAccountIds.Contains(x.AccountId));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = query.Trim().ToLowerInvariant();
            gameQuery = gameQuery.Where(x => x.Name.ToLower().Contains(normalized));
        }

        var games = await gameQuery.ToListAsync(cancellationToken);
        var sourceLogins = familyAccounts.ToDictionary(x => x.Id, x => x.LoginName);

        var dedup = games
            .GroupBy(x => x.AppId)
            .Select(group =>
            {
                var ownRow = group.FirstOrDefault(x => x.AccountId == account.Id);
                var selected = ownRow ?? group
                    .OrderByDescending(x => x.PlaytimeMinutes)
                    .ThenBy(x => x.Name)
                    .First();

                var availability = selected.AccountId == account.Id
                    ? AccountGameAvailability.Owned
                    : AccountGameAvailability.FamilyGroup;

                return new AccountGameDto
                {
                    AppId = selected.AppId,
                    Name = selected.Name,
                    PlaytimeMinutes = selected.PlaytimeMinutes,
                    ImgIconUrl = selected.ImgIconUrl,
                    SourceAccountId = selected.AccountId,
                    SourceLoginName = sourceLogins.GetValueOrDefault(selected.AccountId, selected.AccountId.ToString()),
                    Availability = availability,
                    LastSyncedAt = selected.LastSyncedAt
                };
            })
            .OrderBy(x => x.Name)
            .ToList();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var skip = (page - 1) * pageSize;

        return new AccountGamesPageResult
        {
            TotalCount = dedup.Count,
            Page = page,
            PageSize = pageSize,
            Items = dedup.Skip(skip).Take(pageSize).ToArray()
        };
    }

    public async Task<FriendInviteLinkDto?> GetFriendInviteLinkAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (account is null)
        {
            return null;
        }

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        return BuildFriendInviteLinkDto(account.Id, metadata);
    }

    public async Task<FriendInviteLinkDto> SyncFriendInviteLinkAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        SteamFriendInviteLink invite;
        try
        {
            var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                account,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: false,
                cancellationToken);

            try
            {
                invite = await steamGateway.GetFriendInviteLinkAsync(sessionPayload, cancellationToken);
            }
            catch (SteamGatewayOperationException ex) when (ShouldForceReauthRetry(ex.ReasonCode, ex.Retryable))
            {
                sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                    account,
                    actorId,
                    ip,
                    currentPassword: null,
                    forceReauthenticate: true,
                    cancellationToken);
                invite = await steamGateway.GetFriendInviteLinkAsync(sessionPayload, cancellationToken);
            }
        }
        catch (SteamGatewayOperationException ex)
        {
            ApplyGatewayFailureState(account, ex.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        metadata["friends.invite.url"] = invite.InviteUrl;
        metadata["friends.invite.code"] = invite.InviteCode;
        metadata["friends.invite.token"] = invite.InviteToken;
        metadata["friends.invite.syncedAt"] = invite.SyncedAt.ToString("O", CultureInfo.InvariantCulture);

        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FriendInviteSynced,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["inviteCode"] = invite.InviteCode
            },
            cancellationToken);

        return BuildFriendInviteLinkDto(account.Id, metadata);
    }

    public async Task<SteamOperationResult> AcceptFriendInviteAsync(
        Guid id,
        string inviteUrl,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        if (string.IsNullOrWhiteSpace(inviteUrl))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Invite URL is required.",
                ReasonCode = SteamReasonCodes.InvalidInviteLink
            };
        }

        var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
            account,
            actorId,
            ip,
            currentPassword: null,
            forceReauthenticate: false,
            cancellationToken);
        var result = await steamGateway.AcceptFriendInviteAsync(sessionPayload, inviteUrl, cancellationToken);
        if (!result.Success && ShouldForceReauthRetry(result.ReasonCode, result.Retryable))
        {
            sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                account,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: true,
                cancellationToken);
            result = await steamGateway.AcceptFriendInviteAsync(sessionPayload, inviteUrl, cancellationToken);
        }

        if (!result.Success)
        {
            ApplyGatewayFailureState(account, result.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);

            await auditService.WriteAsync(
                AuditEventType.FriendConnectFailed,
                "steam_account",
                account.Id.ToString(),
                actorId,
                ip,
                new Dictionary<string, string>
                {
                    ["reasonCode"] = result.ReasonCode ?? SteamReasonCodes.Unknown,
                    ["retryable"] = result.Retryable.ToString()
                },
                cancellationToken);

            return result;
        }

        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        if (account.Status is AccountStatus.Error or AccountStatus.RequiresRelogin)
        {
            account.Status = AccountStatus.Active;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FriendInviteAccepted,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            cancellationToken: cancellationToken);

        return result;
    }

    public async Task<AccountFriendsSnapshotDto> RefreshFriendsAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
            account,
            actorId,
            ip,
            currentPassword: null,
            forceReauthenticate: false,
            cancellationToken);
        SteamFriendsSnapshot snapshot;
        try
        {
            snapshot = await steamGateway.GetFriendsSnapshotAsync(sessionPayload, cancellationToken);
        }
        catch (SteamGatewayOperationException ex) when (ShouldForceReauthRetry(ex.ReasonCode, ex.Retryable))
        {
            sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                account,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: true,
                cancellationToken);
            snapshot = await steamGateway.GetFriendsSnapshotAsync(sessionPayload, cancellationToken);
        }

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        var friendsJson = JsonSerializer.Serialize(snapshot.Friends, JsonSerialization.Defaults);
        metadata["friends.snapshot"] = friendsJson;
        metadata["friends.syncedAt"] = snapshot.SyncedAt.ToString("O", CultureInfo.InvariantCulture);

        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "friends_refresh",
                ["friends"] = snapshot.Friends.Count.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        return BuildFriendsSnapshotDto(account.Id, metadata);
    }

    public async Task<AccountFriendsSnapshotDto> GetFriendsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        return BuildFriendsSnapshotDto(account.Id, metadata);
    }

    public async Task<AccountDto?> AssignParentAsync(
        Guid id,
        Guid parentAccountId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        if (id == parentAccountId)
        {
            throw new InvalidOperationException("Account cannot be parent of itself.");
        }

        var account = await dbContext.SteamAccounts
            .Include(x => x.ChildAccounts)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var parent = await dbContext.SteamAccounts
            .Include(x => x.ChildAccounts)
            .FirstOrDefaultAsync(x => x.Id == parentAccountId, cancellationToken)
            ?? throw new InvalidOperationException("Parent account not found.");

        if (account.ParentAccountId == parentAccountId)
        {
            return await GetByIdAsync(id, cancellationToken);
        }

        if (account.ChildAccounts.Count > 0)
        {
            throw new InvalidOperationException("Account that already has children cannot become a family child.");
        }

        if (parent.ParentAccountId is not null)
        {
            throw new InvalidOperationException("Selected parent account is already a child in another family group.");
        }

        account.ParentAccountId = parent.Id;
        account.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FamilyParentAssigned,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["parentAccountId"] = parent.Id.ToString(),
                ["parentLogin"] = parent.LoginName
            },
            cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<AccountDto?> RemoveParentAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (account is null)
        {
            return null;
        }

        if (account.ParentAccountId is null)
        {
            return await GetByIdAsync(id, cancellationToken);
        }

        account.ParentAccountId = null;
        account.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FamilyParentRemoved,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            cancellationToken: cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    private async Task<Dictionary<Guid, int>> BuildFamilyCountsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var roots = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.Id))
            .Select(x => x.ParentAccountId ?? x.Id)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        if (roots.Length == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var counts = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => roots.Contains(x.Id) || (x.ParentAccountId != null && roots.Contains(x.ParentAccountId.Value)))
            .GroupBy(x => x.ParentAccountId ?? x.Id)
            .Select(g => new { RootId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.RootId, x => x.Count);
    }

    private static FriendInviteLinkDto BuildFriendInviteLinkDto(Guid accountId, IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue("friends.invite.url", out var inviteUrl);
        metadata.TryGetValue("friends.invite.code", out var inviteCode);
        metadata.TryGetValue("friends.invite.syncedAt", out var syncedAtRaw);
        var syncedAt = TryParseDate(syncedAtRaw);

        return new FriendInviteLinkDto
        {
            AccountId = accountId,
            InviteUrl = inviteUrl,
            InviteCode = inviteCode,
            LastSyncedAt = syncedAt
        };
    }

    private static AccountFriendsSnapshotDto BuildFriendsSnapshotDto(Guid accountId, IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue("friends.syncedAt", out var syncedAtRaw);
        metadata.TryGetValue("friends.snapshot", out var friendsRaw);

        var items = Array.Empty<AccountFriendDto>();
        if (!string.IsNullOrWhiteSpace(friendsRaw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<SteamFriend>>(friendsRaw, JsonSerialization.Defaults) ?? [];
                items = parsed
                    .Select(x => new AccountFriendDto
                    {
                        SteamId64 = x.SteamId64,
                        PersonaName = x.PersonaName,
                        ProfileUrl = x.ProfileUrl
                    })
                    .OrderBy(x => x.PersonaName ?? x.SteamId64, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (JsonException)
            {
                items = [];
            }
        }

        return new AccountFriendsSnapshotDto
        {
            AccountId = accountId,
            LastSyncedAt = TryParseDate(syncedAtRaw),
            Friends = items
        };
    }

    private static bool TryGetConfirmationContext(SteamOperationResult result, out string? context)
    {
        context = null;
        if (result.Data.Count == 0)
        {
            return false;
        }

        if (result.Data.TryGetValue("confirmationContext", out var direct) &&
            !string.IsNullOrWhiteSpace(direct))
        {
            context = direct.Trim();
            return true;
        }

        return false;
    }

    private static bool TryReadPendingPasswordChange(
        IReadOnlyDictionary<string, string> metadata,
        out PendingPasswordChangeState pending)
    {
        pending = default!;
        if (!metadata.TryGetValue(PasswordPendingRequestIdKey, out var requestId) ||
            string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        if (!metadata.TryGetValue(PasswordPendingCurrentPasswordKey, out var currentPasswordEnc) ||
            string.IsNullOrWhiteSpace(currentPasswordEnc) ||
            !metadata.TryGetValue(PasswordPendingNewPasswordKey, out var newPasswordEnc) ||
            string.IsNullOrWhiteSpace(newPasswordEnc))
        {
            return false;
        }

        metadata.TryGetValue(PasswordPendingContextKey, out var confirmationContext);
        metadata.TryGetValue(PasswordPendingDeauthorizeKey, out var deauthorizeRaw);
        metadata.TryGetValue(PasswordPendingGeneratedKey, out var generatedRaw);
        metadata.TryGetValue(PasswordPendingExpiresAtKey, out var expiresRaw);

        var deauthorize = bool.TryParse(deauthorizeRaw, out var parsedDeauthorize) && parsedDeauthorize;
        var generated = bool.TryParse(generatedRaw, out var parsedGenerated) && parsedGenerated;
        var expiresAt = DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedExpires)
            ? parsedExpires
            : DateTimeOffset.UtcNow;

        pending = new PendingPasswordChangeState(
            requestId.Trim(),
            string.IsNullOrWhiteSpace(confirmationContext) ? null : confirmationContext.Trim(),
            currentPasswordEnc,
            newPasswordEnc,
            deauthorize,
            generated,
            expiresAt);
        return true;
    }

    private static void SetPendingPasswordChange(
        IDictionary<string, string> metadata,
        PendingPasswordChangeState pending)
    {
        metadata[PasswordPendingRequestIdKey] = pending.RequestId;
        metadata[PasswordPendingCurrentPasswordKey] = pending.EncryptedCurrentPassword;
        metadata[PasswordPendingNewPasswordKey] = pending.EncryptedNewPassword;
        metadata[PasswordPendingDeauthorizeKey] = pending.DeauthorizeAfterChange.ToString();
        metadata[PasswordPendingGeneratedKey] = pending.Generated.ToString();
        metadata[PasswordPendingExpiresAtKey] = pending.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(pending.ConfirmationContext))
        {
            metadata.Remove(PasswordPendingContextKey);
        }
        else
        {
            metadata[PasswordPendingContextKey] = pending.ConfirmationContext!;
        }
    }

    private static void ClearPendingPasswordChange(IDictionary<string, string> metadata)
    {
        metadata.Remove(PasswordPendingRequestIdKey);
        metadata.Remove(PasswordPendingContextKey);
        metadata.Remove(PasswordPendingCurrentPasswordKey);
        metadata.Remove(PasswordPendingNewPasswordKey);
        metadata.Remove(PasswordPendingDeauthorizeKey);
        metadata.Remove(PasswordPendingGeneratedKey);
        metadata.Remove(PasswordPendingExpiresAtKey);
    }

    private static void RemoveSensitiveMetadataForOutput(IDictionary<string, string> metadata)
    {
        metadata.Remove(PasswordPendingCurrentPasswordKey);
        metadata.Remove(PasswordPendingNewPasswordKey);
        metadata.Remove(PasswordPendingContextKey);
    }

    private static void ApplyGatewayFailureState(SteamAccount account, string? reasonCode, string actorId)
    {
        account.LastErrorAt = DateTimeOffset.UtcNow;
        account.UpdatedBy = actorId;
        account.Status = reasonCode switch
        {
            SteamReasonCodes.AuthSessionMissing or SteamReasonCodes.GuardPending => AccountStatus.RequiresRelogin,
            SteamReasonCodes.Timeout or SteamReasonCodes.AntiBotBlocked => AccountStatus.Error,
            SteamReasonCodes.EndpointRejected => AccountStatus.Error,
            _ => AccountStatus.Error
        };
    }

    private static DateTimeOffset? TryParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static bool ShouldForceReauthRetry(string? reasonCode, bool retryable)
    {
        return retryable &&
               string.Equals(reasonCode, SteamReasonCodes.AuthSessionMissing, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> EnsureSessionPayloadForGatewayAsync(
        SteamAccount account,
        string actorId,
        string? ip,
        string? currentPassword,
        bool forceReauthenticate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EnsureSessionPayloadAsync(
                account,
                actorId,
                ip,
                currentPassword,
                cancellationToken,
                forceReauthenticate: forceReauthenticate);
        }
        catch (InvalidOperationException ex)
        {
            throw new SteamGatewayOperationException(
                ex.Message,
                SteamReasonCodes.AuthSessionMissing,
                retryable: true,
                ex);
        }
    }

    private async Task<string> EnsureSessionPayloadAsync(
        SteamAccount account,
        string actorId,
        string? ip,
        string? currentPassword,
        CancellationToken cancellationToken,
        bool forceReauthenticate = false)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        var existingSession = await ReadSecretAsync(account, account.Secret.EncryptedSessionPayload, "session_payload", actorId, ip, cancellationToken);
        if (!forceReauthenticate && !string.IsNullOrWhiteSpace(existingSession))
        {
            if (TryNormalizeSessionPayload(existingSession, account.LoginName, account.SteamId64, out var normalizedSession))
            {
                existingSession = normalizedSession;
                account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(normalizedSession);
                account.Secret.EncryptionVersion = cryptoService.Version;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var validation = await steamGateway.ValidateSessionAsync(existingSession, cancellationToken);
            if (validation.IsValid)
            {
                return existingSession;
            }

            try
            {
                var refreshed = await steamGateway.RefreshSessionAsync(existingSession, cancellationToken);
                if (!string.IsNullOrWhiteSpace(refreshed.CookiePayload))
                {
                    account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(refreshed.CookiePayload);
                    account.Secret.EncryptionVersion = cryptoService.Version;
                    account.LastCheckAt = DateTimeOffset.UtcNow;
                    account.LastSuccessAt = DateTimeOffset.UtcNow;
                    account.LastErrorAt = null;
                    account.Status = AccountStatus.Active;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return refreshed.CookiePayload;
                }
            }
            catch
            {
                // fall through to re-auth
            }
        }

        var password = currentPassword
                       ?? await ReadSecretAsync(account, account.Secret.EncryptedPassword, "password", actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Session is missing and password is not configured.");
        }

        var sharedSecret = await ReadSecretAsync(account, account.Secret.EncryptedSharedSecret, "shared_secret", actorId, ip, cancellationToken);
        var identitySecret = await ReadSecretAsync(account, account.Secret.EncryptedIdentitySecret, "identity_secret", actorId, ip, cancellationToken);
        var guardData = await ReadSecretAsync(account, account.Secret.EncryptedRecoveryPayload, "guard_data", actorId, ip, cancellationToken);

        var authResult = await steamGateway.AuthenticateAsync(new SteamCredentials
        {
            LoginName = account.LoginName,
            Password = password,
            SharedSecret = sharedSecret,
            IdentitySecret = identitySecret,
            GuardData = guardData,
            AllowDeviceConfirmation = true
        }, cancellationToken);

        if (!authResult.Success || string.IsNullOrWhiteSpace(authResult.Session.CookiePayload))
        {
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.RequiresRelogin;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(authResult.ErrorMessage ?? "Steam authentication failed.");
        }

        await ApplyAuthResultAsync(
            account,
            authResult,
            password,
            sharedSecret,
            identitySecret,
            actorId,
            ip,
            cancellationToken);

        return authResult.Session.CookiePayload;
    }

    private static bool TryNormalizeSessionPayload(string payload, string loginName, string? steamId64, out string normalized)
    {
        normalized = payload;
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(loginName))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root)
            {
                return false;
            }

            var changed = false;
            if (string.IsNullOrWhiteSpace(root["AccountName"]?.GetValue<string?>()))
            {
                root["AccountName"] = loginName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(root["LoginName"]?.GetValue<string?>()))
            {
                root["LoginName"] = loginName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(steamId64) &&
                string.IsNullOrWhiteSpace(root["SteamId64"]?.GetValue<string?>()))
            {
                root["SteamId64"] = steamId64;
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            normalized = root.ToJsonString(JsonSerialization.Defaults);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string GenerateStrongPassword(int length = 20)
    {
        if (length < 12)
        {
            length = 12;
        }

        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        // Steam password flows behave more reliably with a conservative symbol set.
        const string special = "!@#$*_-";
        var all = upper + lower + digits + special;

        var chars = new List<char>(length)
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            special[RandomNumberGenerator.GetInt32(special.Length)]
        };

        while (chars.Count < length)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars.ToArray());
    }

    private async Task ApplyUpsertAsync(SteamAccount account, AccountUpsertRequest request, string actorId, CancellationToken cancellationToken)
    {
        account.DisplayName = request.DisplayName;
        account.Email = request.Email;
        account.PhoneMasked = request.PhoneMasked;
        account.Note = request.Note;
        account.Proxy = request.Proxy;
        account.Status = request.Status;
        account.UpdatedBy = actorId;

        if (request.Metadata.Count > 0)
        {
            account.MetadataJson = JsonSerializer.Serialize(request.Metadata, JsonSerialization.Defaults);
        }

        if (!string.IsNullOrWhiteSpace(request.FolderName))
        {
            var folderName = request.FolderName.Trim();
            var folder = await dbContext.Folders.FirstOrDefaultAsync(x => x.Name == folderName, cancellationToken);
            if (folder is null)
            {
                folder = new Folder { Name = folderName };
                await dbContext.Folders.AddAsync(folder, cancellationToken);
            }

            account.Folder = folder;
            account.FolderId = folder.Id;
        }

        var normalizedTags = request.Tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedTags.Length > 0)
        {
            account.TagLinks.Clear();

            var existingTags = await dbContext.SteamAccountTags
                .Where(x => normalizedTags.Contains(x.Name))
                .ToListAsync(cancellationToken);

            var newTagNames = normalizedTags.Except(existingTags.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var newTagName in newTagNames)
            {
                var tag = new SteamAccountTag { Name = newTagName };
                existingTags.Add(tag);
                await dbContext.SteamAccountTags.AddAsync(tag, cancellationToken);
            }

            foreach (var tag in existingTags)
            {
                account.TagLinks.Add(new SteamAccountTagLink { Account = account, Tag = tag, AccountId = account.Id, TagId = tag.Id });
            }
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            account.Secret.EncryptedPassword = cryptoService.Encrypt(request.Password);
        }

        if (!string.IsNullOrWhiteSpace(request.SharedSecret))
        {
            account.Secret.EncryptedSharedSecret = cryptoService.Encrypt(request.SharedSecret);
        }

        if (!string.IsNullOrWhiteSpace(request.IdentitySecret))
        {
            account.Secret.EncryptedIdentitySecret = cryptoService.Encrypt(request.IdentitySecret);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionPayload))
        {
            account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(request.SessionPayload);
        }

        if (!string.IsNullOrWhiteSpace(request.RecoveryPayload))
        {
            account.Secret.EncryptedRecoveryPayload = cryptoService.Encrypt(request.RecoveryPayload);
        }

        account.Secret.EncryptionVersion = cryptoService.Version;
    }

    private AccountDto MapAccountDto(SteamAccount entity, int familyCount)
    {
        var metadata = JsonSerialization.DeserializeDictionary(entity.MetadataJson);
        RemoveSensitiveMetadataForOutput(metadata);

        return new AccountDto
        {
            Id = entity.Id,
            LoginName = entity.LoginName,
            DisplayName = entity.DisplayName,
            SteamId64 = entity.SteamId64,
            ProfileUrl = entity.ProfileUrl,
            Email = entity.Email,
            PhoneMasked = entity.PhoneMasked,
            Note = entity.Note,
            Proxy = entity.Proxy,
            FolderId = entity.FolderId,
            FolderName = entity.Folder?.Name,
            ParentAccountId = entity.ParentAccountId,
            ParentLoginName = entity.ParentAccount?.LoginName,
            ChildAccountsCount = entity.ChildAccounts.Count,
            FamilyAccountsCount = familyCount,
            Status = entity.Status,
            GamesLastSyncAt = entity.GamesLastSyncAt,
            GamesCount = entity.GamesCount,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            LastCheckAt = entity.LastCheckAt,
            LastSuccessAt = entity.LastSuccessAt,
            LastErrorAt = entity.LastErrorAt,
            CreatedBy = entity.CreatedBy,
            UpdatedBy = entity.UpdatedBy,
            Metadata = metadata,
            Tags = entity.TagLinks
                .Where(x => x.Tag is not null)
                .Select(x => x.Tag!.Name)
                .OrderBy(x => x)
                .ToList()
        };
    }

    private async Task<string?> GetSessionPayloadAsync(SteamAccount account, string actorId, string? ip, CancellationToken cancellationToken)
    {
        if (account.Secret is null)
        {
            return null;
        }

        await auditService.WriteAsync(
            AuditEventType.SecretRead,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["field"] = "session_payload" },
            cancellationToken);

        return cryptoService.Decrypt(account.Secret.EncryptedSessionPayload);
    }

    private async Task<string?> ReadSecretAsync(
        SteamAccount account,
        string? encryptedValue,
        string fieldName,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
        {
            return null;
        }

        await auditService.WriteAsync(
            AuditEventType.SecretRead,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["field"] = fieldName },
            cancellationToken);

        return cryptoService.Decrypt(encryptedValue);
    }

    private async Task ApplyAuthResultAsync(
        SteamAccount account,
        SteamAuthResult authResult,
        string? plaintextPassword,
        string? plaintextSharedSecret,
        string? plaintextIdentitySecret,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        if (!string.IsNullOrWhiteSpace(plaintextPassword))
        {
            account.Secret.EncryptedPassword = cryptoService.Encrypt(plaintextPassword);
        }

        if (!string.IsNullOrWhiteSpace(plaintextSharedSecret))
        {
            account.Secret.EncryptedSharedSecret = cryptoService.Encrypt(plaintextSharedSecret);
        }

        if (!string.IsNullOrWhiteSpace(plaintextIdentitySecret))
        {
            account.Secret.EncryptedIdentitySecret = cryptoService.Encrypt(plaintextIdentitySecret);
        }

        if (!string.IsNullOrWhiteSpace(authResult.Session.CookiePayload))
        {
            account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(authResult.Session.CookiePayload);
        }

        if (!string.IsNullOrWhiteSpace(authResult.GuardData))
        {
            account.Secret.EncryptedRecoveryPayload = cryptoService.Encrypt(authResult.GuardData);
        }

        account.Secret.EncryptionVersion = cryptoService.Version;

        if (!string.IsNullOrWhiteSpace(authResult.SteamId64))
        {
            account.SteamId64 = authResult.SteamId64;
        }

        if (!string.IsNullOrWhiteSpace(authResult.AccountName))
        {
            account.DisplayName = authResult.AccountName;
        }

        account.LastCheckAt = DateTimeOffset.UtcNow;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["operation"] = "authenticate" },
            cancellationToken);
    }

    private async Task<List<AccountUpsertRequest>> ParseImportAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await new StreamReader(stream).ReadToEndAsync(cancellationToken);
            return JsonSerializer.Deserialize<List<AccountUpsertRequest>>(json, JsonSerialization.Defaults) ?? [];
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.ToLowerInvariant().Trim(),
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim
        });

        var records = csv.GetRecords<dynamic>().ToList();
        var list = new List<AccountUpsertRequest>(records.Count);

        foreach (var record in records)
        {
            var dict = (IDictionary<string, object>)record;
            dict.TryGetValue("login", out var login);
            if (login is null || string.IsNullOrWhiteSpace(login.ToString()))
            {
                continue;
            }

            dict.TryGetValue("displayname", out var displayName);
            dict.TryGetValue("password", out var password);
            dict.TryGetValue("email", out var email);
            dict.TryGetValue("tags", out var tags);
            dict.TryGetValue("folder", out var folder);
            dict.TryGetValue("note", out var note);
            dict.TryGetValue("proxy", out var proxy);

            list.Add(new AccountUpsertRequest
            {
                LoginName = login.ToString()!,
                DisplayName = displayName?.ToString(),
                Password = password?.ToString(),
                Email = email?.ToString(),
                FolderName = folder?.ToString(),
                Note = note?.ToString(),
                Proxy = proxy?.ToString(),
                Tags = tags?.ToString()?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? []
            });
        }

        return list;
    }
}
