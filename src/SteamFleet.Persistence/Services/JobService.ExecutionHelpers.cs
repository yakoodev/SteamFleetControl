using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;

namespace SteamFleet.Persistence.Services;

public sealed partial class JobService
{
    private async Task<SessionResolutionResult> EnsureSessionPayloadAsync(
        SteamAccount account,
        CancellationToken cancellationToken,
        bool forceReauth = false)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        var existingSession = await DecryptSecretAsync(account, account.Secret.EncryptedSessionPayload, "session_payload", cancellationToken);
        if (!forceReauth && !string.IsNullOrWhiteSpace(existingSession))
        {
            if (TryNormalizeSessionPayload(existingSession, account.LoginName, account.SteamId64, out var normalizedSession))
            {
                existingSession = normalizedSession;
                account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(normalizedSession);
                account.Secret.EncryptionVersion = cryptoService.Version;
            }

            var validation = await steamGateway.ValidateSessionAsync(existingSession, cancellationToken);
            account.LastCheckAt = DateTimeOffset.UtcNow;
            if (validation.IsValid)
            {
                return SessionResolutionResult.FromPayload(existingSession);
            }

            try
            {
                var refreshed = await steamGateway.RefreshSessionAsync(existingSession, cancellationToken);
                if (!string.IsNullOrWhiteSpace(refreshed.CookiePayload))
                {
                    account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(refreshed.CookiePayload);
                    account.Secret.EncryptionVersion = cryptoService.Version;
                    account.Status = AccountStatus.Active;
                    account.LastSuccessAt = DateTimeOffset.UtcNow;
                    account.LastErrorAt = null;
                    return SessionResolutionResult.FromPayload(refreshed.CookiePayload);
                }
            }
            catch
            {
                // ignored, fallback to full re-auth.
            }
        }

        var password = await DecryptSecretAsync(account, account.Secret.EncryptedPassword, "password", cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            return SessionResolutionResult.Fail(
                "Session missing and password not configured.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        var sharedSecret = await DecryptSecretAsync(account, account.Secret.EncryptedSharedSecret, "shared_secret", cancellationToken);
        var identitySecret = await DecryptSecretAsync(account, account.Secret.EncryptedIdentitySecret, "identity_secret", cancellationToken);
        var guardData = await DecryptSecretAsync(account, account.Secret.EncryptedRecoveryPayload, "guard_data", cancellationToken);

        var auth = await steamGateway.AuthenticateAsync(new SteamCredentials
        {
            LoginName = account.LoginName,
            Password = password,
            SharedSecret = sharedSecret,
            IdentitySecret = identitySecret,
            GuardData = guardData,
            AllowDeviceConfirmation = true
        }, cancellationToken);

        if (!auth.Success || string.IsNullOrWhiteSpace(auth.Session.CookiePayload))
        {
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.RequiresRelogin;
            return SessionResolutionResult.Fail(
                auth.ErrorMessage ?? "Steam authentication failed.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(auth.Session.CookiePayload);
        if (!string.IsNullOrWhiteSpace(auth.GuardData))
        {
            account.Secret.EncryptedRecoveryPayload = cryptoService.Encrypt(auth.GuardData);
        }

        account.Secret.EncryptionVersion = cryptoService.Version;
        account.Status = AccountStatus.Active;
        account.LastCheckAt = DateTimeOffset.UtcNow;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.SteamId64 = string.IsNullOrWhiteSpace(auth.SteamId64) ? account.SteamId64 : auth.SteamId64;

        return SessionResolutionResult.FromPayload(auth.Session.CookiePayload);
    }

    private async Task<string?> DecryptSecretAsync(
        SteamAccount account,
        string? encryptedValue,
        string fieldName,
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
            "job-worker",
            null,
            new Dictionary<string, string> { ["field"] = fieldName },
            cancellationToken);

        return cryptoService.Decrypt(encryptedValue);
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

    private static string BuildSensitiveReportCsv(IReadOnlyCollection<PasswordReportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("accountId,login,newPassword,deauthorized");
        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(row.AccountId.ToString()));
            sb.Append(',');
            sb.Append(EscapeCsv(row.LoginName));
            sb.Append(',');
            sb.Append(EscapeCsv(row.NewPassword));
            sb.Append(',');
            sb.Append(EscapeCsv(row.Deauthorized.ToString()));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"'))
        {
            value = value.Replace("\"", "\"\"");
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? $"\"{value}\""
            : value;
    }

    private static string GenerateStrongPassword(int length)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        // Keep symbol set aligned with single-account password generator.
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

    private static List<FriendPair> ParseFriendPairs(string? raw)
    {
        var pairs = new List<FriendPair>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return pairs;
        }

        var tokens = raw
            .Split([';', '\r', '\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var normalized = token.Replace("=>", ":", StringComparison.Ordinal)
                .Replace("->", ":", StringComparison.Ordinal)
                .Replace(">", ":", StringComparison.Ordinal);

            var parts = normalized.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !Guid.TryParse(parts[0], out var sourceAccountId) ||
                !Guid.TryParse(parts[1], out var targetAccountId) ||
                sourceAccountId == targetAccountId)
            {
                continue;
            }

            pairs.Add(new FriendPair(sourceAccountId, targetAccountId));
        }

        return pairs
            .Distinct()
            .ToList();
    }

    private static List<Guid> ParseGuidPipe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(['|', ',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Guid.TryParse(x, out var parsed) ? parsed : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
    }

    private static bool IsRetryable(SteamOperationResult result)
    {
        if (result.Retryable)
        {
            return true;
        }

        if (string.Equals(result.ReasonCode, SteamReasonCodes.Timeout, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.AuthSessionMissing, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.AccessDenied, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.AntiBotBlocked, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.CooldownActive, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.AuthThrottled, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.SessionReplaced, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsRecoverableError(result.ErrorMessage);
    }

    private static bool IsRecoverableError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        var text = errorMessage.ToLowerInvariant();
        return text.Contains("timeout", StringComparison.Ordinal) ||
               text.Contains("tempor", StringComparison.Ordinal) ||
               text.Contains("rate limit", StringComparison.Ordinal) ||
               text.Contains("too many", StringComparison.Ordinal) ||
               text.Contains("session", StringComparison.Ordinal) ||
               text.Contains("unauthorized", StringComparison.Ordinal) ||
               text.Contains("expired", StringComparison.Ordinal) ||
               text.Contains("guard", StringComparison.Ordinal) ||
               text.Contains("captcha", StringComparison.Ordinal) ||
               text.Contains("service unavailable", StringComparison.Ordinal);
    }

    private static TimeSpan GetRetryBackoff(int attempt)
    {
        var seconds = Math.Min(60, (int)Math.Pow(2, Math.Max(1, attempt)));
        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class SessionResolutionResult
    {
        public bool Success { get; private init; }
        public string? SessionPayload { get; private init; }
        public string? ErrorMessage { get; private init; }
        public string? ReasonCode { get; private init; }
        public bool Retryable { get; private init; }

        public static SessionResolutionResult FromPayload(string payload) => new()
        {
            Success = true,
            SessionPayload = payload,
            ReasonCode = SteamReasonCodes.None
        };

        public static SessionResolutionResult Fail(string message, string? reasonCode = null, bool retryable = true) => new()
        {
            Success = false,
            ErrorMessage = message,
            ReasonCode = reasonCode ?? SteamReasonCodes.AuthSessionMissing,
            Retryable = retryable
        };
    }

    private sealed class JobItemProcessingResult
    {
        public SteamOperationResult Result { get; init; } = new();
        public string? SensitivePassword { get; init; }

        public static JobItemProcessingResult FromResult(SteamOperationResult result) => new() { Result = result };

        public static JobItemProcessingResult Fail(string message, string? reasonCode = null, bool retryable = false) => new()
        {
            Result = new SteamOperationResult
            {
                Success = false,
                ErrorMessage = message,
                ReasonCode = reasonCode ?? SteamReasonCodes.Unknown,
                Retryable = retryable
            }
        };
    }

    private sealed record FriendPair(Guid SourceAccountId, Guid TargetAccountId);
    private sealed record PasswordReportRow(Guid AccountId, string LoginName, string NewPassword, bool Deauthorized);

    private async Task WriteRiskAuditAsync(
        SteamAccount account,
        AccountRiskTransition transition,
        string actorId,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reasonCode) ? SteamReasonCodes.Unknown : reasonCode;
        if (transition.CooldownScheduled)
        {
            await auditService.WriteAsync(
                AuditEventType.RiskCooldownScheduled,
                "steam_account",
                account.Id.ToString(),
                actorId,
                null,
                new Dictionary<string, string>
                {
                    ["riskLevel"] = account.RiskLevel.ToString(),
                    ["reasonCode"] = normalizedReason,
                    ["autoRetryAfter"] = account.AutoRetryAfter?.ToString("O") ?? string.Empty
                },
                cancellationToken);
        }

        if (transition.CurrentLevel != AccountRiskLevel.Normal)
        {
            await auditService.WriteAsync(
                AuditEventType.RiskSignalDetected,
                "steam_account",
                account.Id.ToString(),
                actorId,
                null,
                new Dictionary<string, string>
                {
                    ["riskLevel"] = account.RiskLevel.ToString(),
                    ["reasonCode"] = normalizedReason,
                    ["authFailStreak"] = account.AuthFailStreak.ToString(),
                    ["riskSignalStreak"] = account.RiskSignalStreak.ToString()
                },
                cancellationToken);
        }
    }

    private async Task MarkRiskRecoveredAsync(
        SteamAccount account,
        string actorId,
        CancellationToken cancellationToken)
    {
        var transition = riskPolicyService.MarkOperationSuccess(account, DateTimeOffset.UtcNow);
        if (!transition.Recovered)
        {
            return;
        }

        await auditService.WriteAsync(
            AuditEventType.RiskRecovered,
            "steam_account",
            account.Id.ToString(),
            actorId,
            null,
            new Dictionary<string, string>
            {
                ["previousLevel"] = transition.PreviousLevel.ToString()
            },
            cancellationToken);
    }

    private static JobDto MapJob(FleetJob job)
    {
        return new JobDto
        {
            Id = job.Id,
            Type = job.Type,
            Status = job.Status,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            FinishedAt = job.FinishedAt,
            CreatedBy = job.CreatedBy,
            TotalCount = job.TotalCount,
            SuccessCount = job.SuccessCount,
            FailureCount = job.FailureCount,
            DryRun = job.DryRun,
            HasSensitiveReport = job.SensitiveReport is not null,
            SensitiveReportConsumed = job.SensitiveReport?.ConsumedAt is not null,
            Payload = JsonSerialization.DeserializeDictionary(job.PayloadJson)
        };
    }
}
