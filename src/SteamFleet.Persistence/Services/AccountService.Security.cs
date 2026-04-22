using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;
using Microsoft.EntityFrameworkCore;

namespace SteamFleet.Persistence.Services;

public sealed partial class AccountService
{
    public async Task<AccountPasswordChangeResult> ChangePasswordAsync(
        Guid id,
        AccountPasswordChangeRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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
            if (!pendingChangeResult.Success &&
                string.Equals(pendingChangeResult.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
            {
                var autoAccepted = await TryAutoAcceptGuardConfirmationsAsync(
                    account,
                    pendingSessionPayload,
                    actorId,
                    ip,
                    cancellationToken,
                    "password",
                    "change",
                    "security");
                if (autoAccepted)
                {
                    pendingChangeResult = await steamGateway.ChangePasswordAsync(
                        pendingSessionPayload,
                        pendingCurrentPassword,
                        pendingNextPassword,
                        request.ConfirmationCode.Trim(),
                        pending.ConfirmationContext,
                        cancellationToken);
                }
            }

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
                    var guardTransition = riskPolicyService.MarkOperationFailure(
                        account,
                        SteamReasonCodes.GuardPending,
                        automated: false,
                        DateTimeOffset.UtcNow);
                    account.UpdatedBy = actorId;
                    account.LastCheckAt = DateTimeOffset.UtcNow;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await WriteRiskAuditAsync(
                        account,
                        guardTransition,
                        actorId,
                        ip,
                        SteamReasonCodes.GuardPending,
                        cancellationToken);
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

                var transition = ApplyGatewayFailureState(account, pendingChangeResult.ReasonCode, actorId);
                await dbContext.SaveChangesAsync(cancellationToken);
                await WriteRiskAuditAsync(
                    account,
                    transition,
                    actorId,
                    ip,
                    pendingChangeResult.ReasonCode ?? SteamReasonCodes.Unknown,
                    cancellationToken);
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
        if (!changeResult.Success &&
            string.Equals(changeResult.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
        {
            var autoAccepted = await TryAutoAcceptGuardConfirmationsAsync(
                account,
                sessionPayload,
                actorId,
                ip,
                cancellationToken,
                "password",
                "change",
                "security");
            if (autoAccepted)
            {
                changeResult = await steamGateway.ChangePasswordAsync(
                    sessionPayload,
                    currentPassword,
                    nextPassword,
                    cancellationToken: cancellationToken);
            }
        }

        if (!changeResult.Success)
        {
            if (string.Equals(changeResult.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
            {
                var guardTransition = riskPolicyService.MarkOperationFailure(
                    account,
                    SteamReasonCodes.GuardPending,
                    automated: false,
                    DateTimeOffset.UtcNow);
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
                await WriteRiskAuditAsync(
                    account,
                    guardTransition,
                    actorId,
                    ip,
                    SteamReasonCodes.GuardPending,
                    cancellationToken);
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
            var transition = ApplyGatewayFailureState(account, changeResult.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                changeResult.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);
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
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
        var result = await steamGateway.DeauthorizeAllSessionsAsync(sessionPayload, cancellationToken);
        if (!result.Success &&
            string.Equals(result.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
        {
            var autoAccepted = await TryAutoAcceptGuardConfirmationsAsync(
                account,
                sessionPayload,
                actorId,
                ip,
                cancellationToken,
                "deauthorize",
                "session",
                "device");
            if (autoAccepted)
            {
                result = await steamGateway.DeauthorizeAllSessionsAsync(sessionPayload, cancellationToken);
            }
        }

        if (!result.Success)
        {
            var transition = ApplyGatewayFailureState(account, result.ReasonCode, actorId);
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.Error;
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                result.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);
            return result;
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedSessionPayload = null;
        account.Status = AccountStatus.RequiresRelogin;
        account.UpdatedBy = actorId;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
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
                var transition = ApplyGatewayFailureState(account, deauth.ReasonCode, actorId);
                account.LastErrorAt = DateTimeOffset.UtcNow;
                account.Status = AccountStatus.Active;
                deauthorizeReasonCode = deauth.ReasonCode;
                deauthorizeRetryable = deauth.Retryable;
                deauthorizeWarning = string.IsNullOrWhiteSpace(deauth.ErrorMessage)
                    ? "Пароль изменён, но Steam не подтвердил завершение сессий."
                    : $"Пароль изменён, но завершить сессии не удалось: {deauth.ErrorMessage}";
                await WriteRiskAuditAsync(
                    account,
                    transition,
                    actorId,
                    ip,
                    deauth.ReasonCode ?? SteamReasonCodes.Unknown,
                    cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(deauthorizeReasonCode))
        {
            await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
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
            WasGenerated = generated,
            Deauthorized = deauthorized,
            ErrorMessage = deauthorizeWarning,
            ReasonCode = deauthorizeReasonCode,
            Retryable = deauthorizeRetryable
        };
    }
}

