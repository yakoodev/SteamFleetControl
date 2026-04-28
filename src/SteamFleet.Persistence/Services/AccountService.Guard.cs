using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;

namespace SteamFleet.Persistence.Services;

public sealed partial class AccountService
{
    private const string GuardLinkStateMetadataKey = "guard.link.state";
    private const string GuardLinkStateUpdatedAtField = "UpdatedAtUtc";
    private const string GuardLinkStateExpiresAtField = "ExpiresAtUtc";
    private static readonly TimeSpan GuardLinkStateTtl = TimeSpan.FromMinutes(30);
    private const int GuardAutoAcceptMinKeywordMatches = 2;

    public async Task<GuardConfirmationsResultDto> GetGuardConfirmationsAsync(
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
        var guardSecrets = await ResolveGuardSecretsAsync(account, actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(guardSecrets.IdentitySecret) || string.IsNullOrWhiteSpace(guardSecrets.DeviceId))
        {
            return new GuardConfirmationsResultDto
            {
                Success = false,
                ErrorMessage = "Для mobile confirmations нужны identity_secret и device_id.",
                ReasonCode = SteamReasonCodes.GuardNotConfigured
            };
        }

        var result = await steamGateway.GetConfirmationsAsync(
            sessionPayload,
            guardSecrets.IdentitySecret,
            guardSecrets.DeviceId,
            cancellationToken);

        if (!result.Success)
        {
            var transition = ApplyGatewayFailureState(account, result.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                result.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);
        }
        else
        {
            account.LastCheckAt = DateTimeOffset.UtcNow;
            account.LastSuccessAt = DateTimeOffset.UtcNow;
            account.LastErrorAt = null;
            account.Status = AccountStatus.Active;
            await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
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
                ["operation"] = "guard_confirmations_get",
                ["success"] = result.Success.ToString(),
                ["count"] = result.Confirmations.Count.ToString()
            },
            cancellationToken);

        return MapGuardConfirmationsResult(result);
    }

    public async Task<SteamOperationResult> AcceptGuardConfirmationAsync(
        Guid id,
        ulong confirmationId,
        ulong confirmationKey,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteGuardConfirmationActionAsync(
            id,
            actorId,
            ip,
            cancellationToken,
            (sessionPayload, identitySecret, deviceId, token) =>
                steamGateway.AcceptConfirmationAsync(
                    sessionPayload,
                    identitySecret,
                    deviceId,
                    confirmationId,
                    confirmationKey,
                    token),
            "guard_confirmation_accept");
    }

    public async Task<SteamOperationResult> DenyGuardConfirmationAsync(
        Guid id,
        ulong confirmationId,
        ulong confirmationKey,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteGuardConfirmationActionAsync(
            id,
            actorId,
            ip,
            cancellationToken,
            (sessionPayload, identitySecret, deviceId, token) =>
                steamGateway.DenyConfirmationAsync(
                    sessionPayload,
                    identitySecret,
                    deviceId,
                    confirmationId,
                    confirmationKey,
                    token),
            "guard_confirmation_deny");
    }

    public async Task<SteamOperationResult> AcceptGuardConfirmationsBatchAsync(
        Guid id,
        IReadOnlyCollection<GuardConfirmationRefDto> confirmations,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteGuardConfirmationActionAsync(
            id,
            actorId,
            ip,
            cancellationToken,
            (sessionPayload, identitySecret, deviceId, token) =>
                steamGateway.AcceptConfirmationsBatchAsync(
                    sessionPayload,
                    identitySecret,
                    deviceId,
                    confirmations.Select(x => new SteamGuardConfirmationRef
                    {
                        Id = x.Id,
                        Key = x.Key
                    }).ToArray(),
                    token),
            "guard_confirmation_accept_batch");
    }

    public async Task<GuardLinkStateDto> StartGuardLinkAsync(
        Guid id,
        GuardLinkStartRequest request,
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
        await EnsureGuardLinkStateNotExpiredAsync(account, actorId, ip, cancellationToken);

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
        var result = await steamGateway.StartAuthenticatorLinkAsync(
            sessionPayload,
            request.PhoneNumber,
            request.PhoneCountryCode,
            cancellationToken);
        await ApplyGuardLinkStateAsync(account, result, actorId, cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "guard_link_start",
                ["step"] = result.Step.ToString(),
                ["success"] = result.Success.ToString()
            },
            cancellationToken);

        return MapGuardLinkState(result);
    }

    public async Task<GuardLinkStateDto> ProvideGuardPhoneAsync(
        Guid id,
        GuardLinkPhoneRequest request,
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
        await EnsureGuardLinkStateNotExpiredAsync(account, actorId, ip, cancellationToken);

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
        var result = await steamGateway.ProvidePhoneForLinkAsync(
            sessionPayload,
            request.PhoneNumber,
            request.PhoneCountryCode,
            cancellationToken);
        await ApplyGuardLinkStateAsync(account, result, actorId, cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "guard_link_phone",
                ["step"] = result.Step.ToString(),
                ["success"] = result.Success.ToString()
            },
            cancellationToken);

        return MapGuardLinkState(result);
    }

    public async Task<GuardLinkStateDto> FinalizeGuardLinkAsync(
        Guid id,
        GuardLinkFinalizeRequest request,
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
        await EnsureGuardLinkStateNotExpiredAsync(account, actorId, ip, cancellationToken);

        var sharedSecret = await ResolveSharedSecretForFinalizeAsync(account, actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(sharedSecret))
        {
            return new GuardLinkStateDto
            {
                Step = SteamGuardLinkStep.Failed.ToString(),
                Success = false,
                ErrorMessage = "Нет shared_secret для финализации линковки.",
                ReasonCode = SteamReasonCodes.GuardNotConfigured
            };
        }

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
        var result = await steamGateway.FinalizeAuthenticatorLinkAsync(
            sessionPayload,
            sharedSecret,
            request.SmsCode,
            cancellationToken);
        await ApplyGuardLinkStateAsync(account, result, actorId, cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "guard_link_finalize",
                ["step"] = result.Step.ToString(),
                ["success"] = result.Success.ToString()
            },
            cancellationToken);

        return MapGuardLinkState(result);
    }

    public async Task<SteamOperationResult> RemoveAuthenticatorAsync(
        Guid id,
        RemoveAuthenticatorRequest request,
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

        var revocation = await ResolveRevocationCodeAsync(account, actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(revocation))
        {
            return Failure(
                "Revocation code is missing for authenticator removal.",
                SteamReasonCodes.GuardNotConfigured);
        }

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
        var result = await steamGateway.RemoveAuthenticatorAsync(
            sessionPayload,
            revocation,
            request.Scheme,
            cancellationToken);
        if (!result.Success)
        {
            var transition = ApplyGatewayFailureState(account, result.ReasonCode, actorId);
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
        account.Secret.EncryptedSharedSecret = null;
        account.Secret.EncryptedIdentitySecret = null;
        account.Secret.EncryptedDeviceId = null;
        account.Secret.EncryptedRevocationCode = null;
        account.Secret.EncryptedSerialNumber = null;
        account.Secret.EncryptedTokenGid = null;
        account.Secret.EncryptedUri = null;
        account.Secret.EncryptedRecoveryPayload = null;
        account.Secret.EncryptedLinkStatePayload = null;
        account.Secret.GuardFullyEnrolled = false;
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.UpdatedBy = actorId;

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        metadata.Remove(GuardLinkStateMetadataKey);
        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "guard_remove",
                ["scheme"] = request.Scheme.ToString(),
                ["success"] = true.ToString()
            },
            cancellationToken);

        return result;
    }

    private async Task<SteamOperationResult> ExecuteGuardConfirmationActionAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken,
        Func<string, string, string, CancellationToken, Task<SteamOperationResult>> action,
        string auditOperation)
    {
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

        var sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
        var guardSecrets = await ResolveGuardSecretsAsync(account, actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(guardSecrets.IdentitySecret) || string.IsNullOrWhiteSpace(guardSecrets.DeviceId))
        {
            return Failure(
                "Для mobile confirmations нужны identity_secret и device_id.",
                SteamReasonCodes.GuardNotConfigured);
        }

        var result = await action(
            sessionPayload,
            guardSecrets.IdentitySecret,
            guardSecrets.DeviceId,
            cancellationToken);

        if (!result.Success)
        {
            var transition = ApplyGatewayFailureState(account, result.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                result.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);
        }
        else
        {
            account.LastCheckAt = DateTimeOffset.UtcNow;
            account.LastSuccessAt = DateTimeOffset.UtcNow;
            account.LastErrorAt = null;
            account.Status = AccountStatus.Active;
            await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
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
                ["operation"] = auditOperation,
                ["success"] = result.Success.ToString()
            },
            cancellationToken);

        return result;
    }

    private async Task<(string? IdentitySecret, string? DeviceId)> ResolveGuardSecretsAsync(
        SteamAccount account,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        var identity = await ReadSecretAsync(account, account.Secret.EncryptedIdentitySecret, "identity_secret", actorId, ip, cancellationToken);
        var deviceId = await ReadSecretAsync(account, account.Secret.EncryptedDeviceId, "device_id", actorId, ip, cancellationToken);
        var recoveryPayload = await ReadSecretAsync(account, account.Secret.EncryptedRecoveryPayload, "guard_data", actorId, ip, cancellationToken);

        if (string.IsNullOrWhiteSpace(identity) &&
            TryReadGuardFieldFromPayload(recoveryPayload, "identity_secret", out var payloadIdentity))
        {
            identity = payloadIdentity;
        }

        if (string.IsNullOrWhiteSpace(deviceId) &&
            TryReadGuardFieldFromPayload(recoveryPayload, "device_id", out var payloadDevice))
        {
            deviceId = payloadDevice;
        }

        var changed = false;
        if (!string.IsNullOrWhiteSpace(identity) && string.IsNullOrWhiteSpace(account.Secret.EncryptedIdentitySecret))
        {
            account.Secret.EncryptedIdentitySecret = cryptoService.Encrypt(identity);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(account.Secret.EncryptedDeviceId))
        {
            account.Secret.EncryptedDeviceId = cryptoService.Encrypt(deviceId);
            changed = true;
        }

        if (changed)
        {
            account.Secret.EncryptionVersion = cryptoService.Version;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return (identity, deviceId);
    }

    private async Task<string?> ResolveSharedSecretForFinalizeAsync(
        SteamAccount account,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        var linkPayload = await ReadSecretAsync(account, account.Secret.EncryptedLinkStatePayload, "guard_link_state", actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(linkPayload))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = TryReadGuardDateTimeFromPayload(linkPayload, GuardLinkStateExpiresAtField);
        if (expiresAt is null &&
            account.Secret.UpdatedAt < now.Subtract(GuardLinkStateTtl))
        {
            expiresAt = account.Secret.UpdatedAt.Add(GuardLinkStateTtl);
        }

        if (expiresAt is not null && expiresAt <= now)
        {
            account.Secret.EncryptedLinkStatePayload = null;
            account.Secret.EncryptionVersion = cryptoService.Version;
            var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
            metadata.Remove(GuardLinkStateMetadataKey);
            account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (TryReadGuardFieldFromPayload(linkPayload, "SharedSecret", out var fromLink))
        {
            return fromLink;
        }

        var shared = await ReadSecretAsync(account, account.Secret.EncryptedSharedSecret, "shared_secret", actorId, ip, cancellationToken);
        return string.IsNullOrWhiteSpace(shared)
            ? null
            : shared;
    }

    private async Task<string?> ResolveRevocationCodeAsync(
        SteamAccount account,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        var revocation = await ReadSecretAsync(account, account.Secret.EncryptedRevocationCode, "revocation_code", actorId, ip, cancellationToken);
        if (!string.IsNullOrWhiteSpace(revocation))
        {
            return revocation;
        }

        var recoveryPayload = await ReadSecretAsync(account, account.Secret.EncryptedRecoveryPayload, "guard_data", actorId, ip, cancellationToken);
        if (TryReadGuardFieldFromPayload(recoveryPayload, "revocation_code", out var fromPayload))
        {
            return fromPayload;
        }

        return null;
    }

    private async Task EnsureGuardLinkStateNotExpiredAsync(
        SteamAccount account,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        var linkPayload = await ReadSecretAsync(
            account,
            account.Secret.EncryptedLinkStatePayload,
            "guard_link_state",
            actorId,
            ip,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(linkPayload))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = TryReadGuardDateTimeFromPayload(linkPayload, GuardLinkStateExpiresAtField);
        if (expiresAt is null &&
            account.Secret.UpdatedAt < now.Subtract(GuardLinkStateTtl))
        {
            expiresAt = account.Secret.UpdatedAt.Add(GuardLinkStateTtl);
        }

        if (expiresAt is null || expiresAt > now)
        {
            return;
        }

        account.Secret.EncryptedLinkStatePayload = null;
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.UpdatedBy = actorId;

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        metadata.Remove(GuardLinkStateMetadataKey);
        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "guard_link_state_expired_cleanup",
                ["expiredAt"] = expiresAt.Value.ToString("O", CultureInfo.InvariantCulture)
            },
            cancellationToken);
    }

    private async Task ApplyGuardLinkStateAsync(
        SteamAccount account,
        SteamGuardLinkState state,
        string actorId,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        if (!string.IsNullOrWhiteSpace(state.SharedSecret))
        {
            account.Secret.EncryptedSharedSecret = cryptoService.Encrypt(state.SharedSecret);
        }

        if (!string.IsNullOrWhiteSpace(state.IdentitySecret))
        {
            account.Secret.EncryptedIdentitySecret = cryptoService.Encrypt(state.IdentitySecret);
        }

        if (!string.IsNullOrWhiteSpace(state.DeviceId))
        {
            account.Secret.EncryptedDeviceId = cryptoService.Encrypt(state.DeviceId);
        }

        if (!string.IsNullOrWhiteSpace(state.RevocationCode))
        {
            account.Secret.EncryptedRevocationCode = cryptoService.Encrypt(state.RevocationCode);
        }

        if (!string.IsNullOrWhiteSpace(state.SerialNumber))
        {
            account.Secret.EncryptedSerialNumber = cryptoService.Encrypt(state.SerialNumber);
        }

        if (!string.IsNullOrWhiteSpace(state.TokenGid))
        {
            account.Secret.EncryptedTokenGid = cryptoService.Encrypt(state.TokenGid);
        }

        if (!string.IsNullOrWhiteSpace(state.Uri))
        {
            account.Secret.EncryptedUri = cryptoService.Encrypt(state.Uri);
        }

        if (!string.IsNullOrWhiteSpace(state.RecoveryPayload))
        {
            account.Secret.EncryptedRecoveryPayload = cryptoService.Encrypt(state.RecoveryPayload);
        }

        if (state.Success && state.Step == SteamGuardLinkStep.Completed)
        {
            account.Secret.GuardFullyEnrolled = true;
            account.Secret.EncryptedLinkStatePayload = null;
            account.Status = AccountStatus.Active;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            var expiresAt = now.Add(GuardLinkStateTtl);
            var linkPayload = new JsonObject
            {
                [GuardLinkStateUpdatedAtField] = now.ToString("O", CultureInfo.InvariantCulture),
                [GuardLinkStateExpiresAtField] = expiresAt.ToString("O", CultureInfo.InvariantCulture),
                ["Step"] = state.Step.ToString(),
                ["Success"] = state.Success,
                ["ErrorMessage"] = state.ErrorMessage,
                ["ReasonCode"] = state.ReasonCode,
                ["Retryable"] = state.Retryable,
                ["FullyEnrolled"] = state.FullyEnrolled,
                ["PhoneNumberHint"] = state.PhoneNumberHint,
                ["ConfirmationEmailAddress"] = state.ConfirmationEmailAddress,
                ["DeviceId"] = state.DeviceId,
                ["SharedSecret"] = state.SharedSecret,
                ["IdentitySecret"] = state.IdentitySecret,
                ["RevocationCode"] = state.RevocationCode,
                ["SerialNumber"] = state.SerialNumber,
                ["TokenGid"] = state.TokenGid,
                ["Uri"] = state.Uri,
                ["RecoveryPayload"] = state.RecoveryPayload
            };
            account.Secret.EncryptedLinkStatePayload = cryptoService.Encrypt(linkPayload.ToJsonString());
            if (state.Step == SteamGuardLinkStep.NeedSmsCode)
            {
                account.Secret.GuardFullyEnrolled = false;
            }
        }

        account.Secret.EncryptionVersion = cryptoService.Version;
        account.UpdatedBy = actorId;

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        if (state.Success && state.Step == SteamGuardLinkStep.Completed)
        {
            metadata.Remove(GuardLinkStateMetadataKey);
        }
        else
        {
            metadata[GuardLinkStateMetadataKey] = state.Step.ToString();
        }

        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static GuardConfirmationsResultDto MapGuardConfirmationsResult(SteamGuardConfirmationsResult result)
    {
        return new GuardConfirmationsResultDto
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            ReasonCode = result.ReasonCode,
            Retryable = result.Retryable,
            NeedAuthentication = result.NeedAuthentication,
            SyncedAt = result.SyncedAt,
            Confirmations = result.Confirmations.Select(x => new GuardConfirmationDto
            {
                Id = x.Id,
                Key = x.Key,
                CreatorId = x.CreatorId,
                Headline = x.Headline,
                Summary = x.Summary,
                AcceptText = x.AcceptText,
                CancelText = x.CancelText,
                IconUrl = x.IconUrl,
                Type = x.Type.ToString()
            }).ToList()
        };
    }

    private static GuardLinkStateDto MapGuardLinkState(SteamGuardLinkState state)
    {
        return new GuardLinkStateDto
        {
            Step = state.Step.ToString(),
            Success = state.Success,
            ErrorMessage = state.ErrorMessage,
            ReasonCode = state.ReasonCode,
            Retryable = state.Retryable,
            FullyEnrolled = state.FullyEnrolled,
            PhoneNumberHint = state.PhoneNumberHint,
            ConfirmationEmailAddress = state.ConfirmationEmailAddress,
            DeviceId = state.DeviceId,
            RevocationCode = state.RevocationCode,
            SerialNumber = state.SerialNumber,
            TokenGid = state.TokenGid,
            Uri = state.Uri
        };
    }

    private static bool TryReadGuardFieldFromPayload(string? payload, string fieldName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject node)
            {
                return false;
            }

            if (node[fieldName]?.GetValue<string?>() is not { } raw || string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            value = raw.Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset? TryReadGuardDateTimeFromPayload(string? payload, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject node)
            {
                return null;
            }

            if (node[fieldName]?.GetValue<string?>() is not { } raw || string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> TryAutoAcceptGuardConfirmationsAsync(
        SteamAccount account,
        string sessionPayload,
        string actorId,
        string? ip,
        CancellationToken cancellationToken,
        string operation,
        IReadOnlyCollection<string> relevanceKeywords,
        IReadOnlyCollection<string?> expectedCreatorSteamIds)
    {
        var guardSecrets = await ResolveGuardSecretsAsync(account, actorId, ip, cancellationToken);
        if (string.IsNullOrWhiteSpace(guardSecrets.IdentitySecret) || string.IsNullOrWhiteSpace(guardSecrets.DeviceId))
        {
            return false;
        }

        var list = await steamGateway.GetConfirmationsAsync(
            sessionPayload,
            guardSecrets.IdentitySecret,
            guardSecrets.DeviceId,
            cancellationToken);
        if (!list.Success || list.Confirmations.Count == 0)
        {
            return false;
        }

        var normalizedKeywords = relevanceKeywords
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expectedCreatorIds = new HashSet<ulong>();
        foreach (var expected in expectedCreatorSteamIds)
        {
            if (string.IsNullOrWhiteSpace(expected))
            {
                continue;
            }

            if (ulong.TryParse(expected.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var creatorId))
            {
                expectedCreatorIds.Add(creatorId);
            }
        }

        if (expectedCreatorIds.Count == 0)
        {
            return false;
        }

        var requiredMatches = normalizedKeywords.Length == 0
            ? 0
            : Math.Min(GuardAutoAcceptMinKeywordMatches, normalizedKeywords.Length);
        var candidates = list.Confirmations
            .Where(x => IsConfirmationRelevantForAutoAccept(x, normalizedKeywords, expectedCreatorIds, requiredMatches))
            .ToArray();

        if (candidates.Length == 0)
        {
            return false;
        }

        var accepted = await steamGateway.AcceptConfirmationsBatchAsync(
            sessionPayload,
            guardSecrets.IdentitySecret,
            guardSecrets.DeviceId,
            candidates
                .Select(x => new SteamGuardConfirmationRef
                {
                    Id = x.Id,
                    Key = x.Key
                })
                .ToArray(),
            cancellationToken);

        if (!accepted.Success)
        {
            return false;
        }

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "guard_auto_accept",
                ["scope"] = operation,
                ["accepted"] = candidates.Length.ToString(),
                ["fetched"] = list.Confirmations.Count.ToString()
            },
            cancellationToken);

        return true;
    }

    private static bool IsConfirmationRelevantForAutoAccept(
        SteamGuardConfirmation confirmation,
        IReadOnlyList<string> keywords,
        IReadOnlySet<ulong> expectedCreatorIds,
        int requiredMatches)
    {
        if (confirmation.Type is SteamGuardConfirmationType.Trade or SteamGuardConfirmationType.MarketListing)
        {
            return false;
        }

        if (confirmation.CreatorId == 0 || !expectedCreatorIds.Contains(confirmation.CreatorId))
        {
            return false;
        }

        if (keywords.Count == 0 || requiredMatches <= 0)
        {
            return true;
        }

        var haystack = BuildConfirmationSearchText(confirmation);
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        var matchCount = keywords.Count(keyword =>
            haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return matchCount >= requiredMatches;
    }

    private static string BuildConfirmationSearchText(SteamGuardConfirmation confirmation)
    {
        return string.Join(
            '\n',
            new[]
            {
                confirmation.Headline ?? string.Empty,
                string.Join('\n', confirmation.Summary),
                confirmation.AcceptText ?? string.Empty,
                confirmation.CancelText ?? string.Empty
            });
    }

    private static SteamOperationResult Failure(string message, string reasonCode, bool retryable = false)
    {
        return new SteamOperationResult
        {
            Success = false,
            ErrorMessage = message,
            ReasonCode = reasonCode,
            Retryable = retryable
        };
    }
}
