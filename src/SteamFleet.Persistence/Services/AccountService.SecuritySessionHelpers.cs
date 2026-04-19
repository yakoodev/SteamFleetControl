using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;

namespace SteamFleet.Persistence.Services;

public sealed partial class AccountService
{
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
}

