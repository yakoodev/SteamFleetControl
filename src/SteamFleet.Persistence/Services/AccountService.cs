using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Settings;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Security;

namespace SteamFleet.Persistence.Services;

public sealed partial class AccountService(
    SteamFleetDbContext dbContext,
    ISteamAccountGateway steamGateway,
    ISecretCryptoService cryptoService,
    IAuditService auditService,
    IAccountRiskPolicyService? accountRiskPolicyService = null,
    IAccountOperationLock? accountOperationLock = null,
    IOperationalSettingsService? optionalOperationalSettingsService = null) : IAccountService
{
    private readonly IAccountRiskPolicyService riskPolicyService = accountRiskPolicyService ?? new AccountRiskPolicyService();
    private readonly IAccountOperationLock operationLock = accountOperationLock ?? new InMemoryAccountOperationLock();
    private readonly IOperationalSettingsService operationalSettingsService =
        optionalOperationalSettingsService ?? new OperationalSettingsService(dbContext, auditService);
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

    public async Task<SteamAuthResult> AuthenticateAsync(
        Guid id,
        AccountAuthenticateRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        SteamAuthResult authResult;
        var shouldSyncFamily = false;

        await using (var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken))
        {
            var account = await dbContext.SteamAccounts
                .Include(x => x.Secret)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Account not found.");

            account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
            await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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

            authResult = await steamGateway.AuthenticateAsync(new SteamCredentials
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
                await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
                shouldSyncFamily = true;
            }
            else
            {
                var transition = riskPolicyService.MarkOperationFailure(
                    account,
                    SteamReasonCodes.AuthSessionMissing,
                    automated: false,
                    DateTimeOffset.UtcNow);
                account.Status = AccountStatus.RequiresRelogin;
                account.LastErrorAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                await WriteRiskAuditAsync(
                    account,
                    transition,
                    actorId,
                    ip,
                    SteamReasonCodes.AuthSessionMissing,
                    cancellationToken);
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
        }

        if (shouldSyncFamily)
        {
            try
            {
                await SyncFamilyFromSteamAsync(id, actorId, ip, cancellationToken);
            }
            catch (Exception ex)
            {
                await auditService.WriteAsync(
                    AuditEventType.SystemError,
                    "steam_account",
                    id.ToString(),
                    actorId,
                    ip,
                    new Dictionary<string, string>
                    {
                        ["operation"] = "family_auto_sync",
                        ["error"] = ex.Message
                    },
                    cancellationToken);
            }
        }

        return authResult;
    }

    public async Task<AccountQrOnboardingStartResult> StartQrOnboardingAsync(
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var start = await steamGateway.StartQrAuthenticationAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_qr_onboarding",
            start.FlowId.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "qr_onboarding_start"
            },
            cancellationToken);

        return new AccountQrOnboardingStartResult
        {
            FlowId = start.FlowId,
            ChallengeUrl = start.ChallengeUrl,
            QrImageDataUrl = BuildQrImageDataUrl(start.ChallengeUrl),
            ExpiresAt = start.ExpiresAt,
            PollingIntervalSeconds = start.PollingIntervalSeconds
        };
    }

    public async Task<AccountQrOnboardingPollResult> PollQrOnboardingAsync(
        Guid flowId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var poll = await steamGateway.PollQrAuthenticationAsync(flowId, cancellationToken);
        var result = new AccountQrOnboardingPollResult
        {
            FlowId = flowId,
            Status = MapOnboardingStatus(poll.Status),
            ErrorMessage = poll.ErrorMessage,
            ReasonCode = MapOnboardingReasonCode(poll.Status, poll.ErrorMessage),
            ExpiresAt = poll.ExpiresAt
        };

        if (poll.Status != SteamQrAuthStatus.Completed)
        {
            await auditService.WriteAsync(
                AuditEventType.AccountUpdated,
                "steam_qr_onboarding",
                flowId.ToString(),
                actorId,
                ip,
                new Dictionary<string, string>
                {
                    ["operation"] = "qr_onboarding_poll",
                    ["status"] = result.Status.ToString(),
                    ["reasonCode"] = result.ReasonCode ?? SteamReasonCodes.None
                },
                cancellationToken);
            return result;
        }

        if (poll.AuthResult is not { Success: true } authResult ||
            string.IsNullOrWhiteSpace(authResult.Session.CookiePayload))
        {
            result.Status = AccountQrOnboardingStatus.Failed;
            result.ReasonCode = SteamReasonCodes.AuthFailed;
            result.ErrorMessage = string.IsNullOrWhiteSpace(poll.ErrorMessage)
                ? "Steam QR авторизация завершилась без валидной сессии."
                : poll.ErrorMessage;
            return result;
        }

        var loginName = !string.IsNullOrWhiteSpace(authResult.AccountName)
            ? authResult.AccountName.Trim()
            : authResult.SteamId64?.Trim();
        if (string.IsNullOrWhiteSpace(loginName))
        {
            result.Status = AccountQrOnboardingStatus.Failed;
            result.ReasonCode = SteamReasonCodes.AuthFailed;
            result.ErrorMessage = "Steam не вернул login/account name для создания карточки.";
            return result;
        }

        var steamId64 = authResult.SteamId64?.Trim();
        var existing = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => x.LoginName == loginName ||
                        (!string.IsNullOrWhiteSpace(steamId64) && x.SteamId64 == steamId64))
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.LoginName, x.SteamId64 })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            result.Status = AccountQrOnboardingStatus.Conflict;
            result.ReasonCode = SteamReasonCodes.DuplicateAccount;
            result.ErrorMessage = "Аккаунт уже существует в базе.";
            result.ExistingAccount = new AccountQrOnboardingExistingAccount
            {
                Id = existing.Id,
                LoginName = existing.LoginName,
                SteamId64 = existing.SteamId64
            };
            return result;
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new SteamAccount
        {
            LoginName = loginName,
            DisplayName = authResult.AccountName,
            SteamId64 = steamId64,
            Status = AccountStatus.Active,
            LastCheckAt = now,
            LastSuccessAt = now,
            LastErrorAt = null,
            CreatedBy = actorId,
            UpdatedBy = actorId,
            MetadataJson = JsonSerialization.SerializeDictionary(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["onboarding"] = "qr",
                ["onboarding.flowId"] = flowId.ToString()
            }),
            Secret = new SteamAccountSecret
            {
                EncryptedSessionPayload = cryptoService.Encrypt(authResult.Session.CookiePayload),
                EncryptedRecoveryPayload = string.IsNullOrWhiteSpace(authResult.GuardData) ? null : cryptoService.Encrypt(authResult.GuardData),
                EncryptionVersion = cryptoService.Version
            }
        };

        try
        {
            await dbContext.SteamAccounts.AddAsync(entity, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var conflict = await dbContext.SteamAccounts
                .AsNoTracking()
                .Where(x => x.LoginName == loginName ||
                            (!string.IsNullOrWhiteSpace(steamId64) && x.SteamId64 == steamId64))
                .OrderBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.LoginName, x.SteamId64 })
                .FirstOrDefaultAsync(cancellationToken);

            if (conflict is not null)
            {
                result.Status = AccountQrOnboardingStatus.Conflict;
                result.ReasonCode = SteamReasonCodes.DuplicateAccount;
                result.ErrorMessage = "Аккаунт уже существует в базе.";
                result.ExistingAccount = new AccountQrOnboardingExistingAccount
                {
                    Id = conflict.Id,
                    LoginName = conflict.LoginName,
                    SteamId64 = conflict.SteamId64
                };
                return result;
            }

            throw;
        }

        var created = await dbContext.SteamAccounts
            .AsNoTracking()
            .Include(x => x.Folder)
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .FirstAsync(x => x.Id == entity.Id, cancellationToken);

        result.Status = AccountQrOnboardingStatus.Completed;
        result.ReasonCode = SteamReasonCodes.None;
        result.ErrorMessage = null;
        result.CreatedAccount = MapAccountDto(created, familyCount: 1);

        await auditService.WriteAsync(
            AuditEventType.AccountCreated,
            "steam_account",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["login"] = entity.LoginName,
                ["operation"] = "qr_onboarding",
                ["flowId"] = flowId.ToString()
            },
            cancellationToken);

        return result;
    }

    public async Task CancelQrOnboardingAsync(
        Guid flowId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await steamGateway.CancelQrAuthenticationAsync(flowId, cancellationToken);
        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_qr_onboarding",
            flowId.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "qr_onboarding_cancel"
            },
            cancellationToken);
    }

    public async Task<SteamQrAuthStartResult> StartQrAuthenticationAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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
        SteamQrAuthPollResult poll;

        await using (var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken))
        {
            var account = await dbContext.SteamAccounts
                .Include(x => x.Secret)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Account not found.");
            await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

            poll = await steamGateway.PollQrAuthenticationAsync(flowId, cancellationToken);
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
                await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
            }
            else if (poll.Status is SteamQrAuthStatus.Failed or SteamQrAuthStatus.Expired)
            {
                var transition = riskPolicyService.MarkOperationFailure(
                    account,
                    SteamReasonCodes.AuthSessionMissing,
                    automated: false,
                    DateTimeOffset.UtcNow);
                account.LastErrorAt = DateTimeOffset.UtcNow;
                account.Status = AccountStatus.RequiresRelogin;
                await dbContext.SaveChangesAsync(cancellationToken);
                await WriteRiskAuditAsync(
                    account,
                    transition,
                    actorId,
                    ip,
                    SteamReasonCodes.AuthSessionMissing,
                    cancellationToken);
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
        }

        return poll;
    }

    public async Task CancelQrAuthenticationAsync(
        Guid id,
        Guid flowId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

        var sessionPayload = await GetSessionPayloadAsync(account, actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(sessionPayload))
        {
            var missingTransition = riskPolicyService.MarkOperationFailure(
                account,
                SteamReasonCodes.AuthSessionMissing,
                automated: false,
                DateTimeOffset.UtcNow);
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.RequiresRelogin;
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                missingTransition,
                actorId,
                ip,
                SteamReasonCodes.AuthSessionMissing,
                cancellationToken);
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

            await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
        }
        else
        {
            var transition = riskPolicyService.MarkOperationFailure(
                account,
                SteamReasonCodes.AuthSessionMissing,
                automated: false,
                DateTimeOffset.UtcNow);
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.RequiresRelogin;
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                SteamReasonCodes.AuthSessionMissing,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<SteamSessionInfo> RefreshSessionAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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
        await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);

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
}

