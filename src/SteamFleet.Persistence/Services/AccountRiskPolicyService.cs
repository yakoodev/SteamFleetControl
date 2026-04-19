using Microsoft.Extensions.Configuration;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;

namespace SteamFleet.Persistence.Services;

public sealed class AccountRiskPolicyService : IAccountRiskPolicyService
{
    private readonly int _minSensitiveIntervalSeconds;
    private readonly int _autoRetryCooldownMinutes;
    private readonly int _guardRetryDelaySeconds;
    private readonly int _maxAuthFailuresBeforeCooldown;

    public AccountRiskPolicyService()
        : this(configuration: null)
    {
    }

    public AccountRiskPolicyService(IConfiguration? configuration)
    {
        _minSensitiveIntervalSeconds = ReadInt(configuration, "SteamGateway:MinSensitiveIntervalSeconds", 3, 0, 30);
        _autoRetryCooldownMinutes = ReadInt(configuration, "SteamGateway:AutoRetryCooldownMinutes", 20, 5, 120);
        _guardRetryDelaySeconds = ReadInt(configuration, "SteamGateway:GuardRetryDelaySeconds", 30, 10, 300);
        _maxAuthFailuresBeforeCooldown = ReadInt(configuration, "SteamGateway:MaxAuthFailuresBeforeCooldown", 3, 1, 10);
    }

    public AccountRiskPrecheck EvaluateBeforeOperation(SteamAccount account, bool automated, DateTimeOffset nowUtc)
    {
        var delay = GetOperationDelay(account, nowUtc);
        if (!automated)
        {
            return new AccountRiskPrecheck(false, null, false, account.AutoRetryAfter, delay);
        }

        if (account.AutoRetryAfter is not null && account.AutoRetryAfter > nowUtc)
        {
            return new AccountRiskPrecheck(
                IsBlocked: true,
                ReasonCode: SteamReasonCodes.CooldownActive,
                Retryable: true,
                RetryAfter: account.AutoRetryAfter,
                Delay: delay);
        }

        return new AccountRiskPrecheck(false, null, false, account.AutoRetryAfter, delay);
    }

    public void MarkOperationStarted(SteamAccount account, DateTimeOffset nowUtc)
    {
        account.LastSensitiveOpAt = nowUtc;
    }

    public AccountRiskTransition MarkOperationSuccess(SteamAccount account, DateTimeOffset nowUtc)
    {
        var previousLevel = account.RiskLevel;
        account.AuthFailStreak = 0;
        account.RiskSignalStreak = 0;
        account.LastRiskReasonCode = null;
        account.LastRiskAt = null;
        account.AutoRetryAfter = null;
        account.RiskLevel = AccountRiskLevel.Normal;
        account.LastSensitiveOpAt = nowUtc;

        return new AccountRiskTransition(
            PreviousLevel: previousLevel,
            CurrentLevel: account.RiskLevel,
            CooldownScheduled: false,
            Recovered: previousLevel != AccountRiskLevel.Normal);
    }

    public AccountRiskTransition MarkOperationFailure(
        SteamAccount account,
        string? reasonCode,
        bool automated,
        DateTimeOffset nowUtc)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reasonCode) ? SteamReasonCodes.Unknown : reasonCode;
        var previousLevel = account.RiskLevel;

        account.LastRiskReasonCode = normalizedReason;
        account.LastRiskAt = nowUtc;
        account.LastSensitiveOpAt = nowUtc;
        account.RiskSignalStreak = Math.Max(1, account.RiskSignalStreak + 1);

        if (IsAuthFailure(normalizedReason))
        {
            account.AuthFailStreak = Math.Max(1, account.AuthFailStreak + 1);
        }

        var shouldScheduleCooldown = false;
        DateTimeOffset? retryAfter = null;

        if (string.Equals(normalizedReason, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
        {
            account.RiskLevel = AccountRiskLevel.Elevated;
            retryAfter = nowUtc.AddSeconds(_guardRetryDelaySeconds);
            shouldScheduleCooldown = true;
        }
        else if (IsHardCooldownReason(normalizedReason))
        {
            account.RiskLevel = AccountRiskLevel.Cooldown;
            retryAfter = nowUtc.Add(GetCooldownDuration(account.RiskSignalStreak));
            shouldScheduleCooldown = true;
        }
        else if (IsSoftRiskReason(normalizedReason))
        {
            account.RiskLevel = AccountRiskLevel.Elevated;
            if (automated && account.RiskSignalStreak >= 2)
            {
                account.RiskLevel = AccountRiskLevel.Cooldown;
                retryAfter = nowUtc.Add(GetCooldownDuration(account.RiskSignalStreak));
                shouldScheduleCooldown = true;
            }
        }
        else
        {
            account.RiskLevel = account.RiskLevel == AccountRiskLevel.Normal
                ? AccountRiskLevel.Elevated
                : account.RiskLevel;
        }

        if (account.AuthFailStreak >= _maxAuthFailuresBeforeCooldown)
        {
            account.RiskLevel = AccountRiskLevel.Cooldown;
            retryAfter = nowUtc.Add(GetCooldownDuration(account.AuthFailStreak));
            shouldScheduleCooldown = true;
        }

        if (shouldScheduleCooldown && retryAfter is not null)
        {
            account.AutoRetryAfter = account.AutoRetryAfter is null || retryAfter > account.AutoRetryAfter
                ? retryAfter
                : account.AutoRetryAfter;
        }

        return new AccountRiskTransition(
            PreviousLevel: previousLevel,
            CurrentLevel: account.RiskLevel,
            CooldownScheduled: shouldScheduleCooldown && account.AutoRetryAfter is not null,
            Recovered: false);
    }

    public string? BuildManualWarning(SteamAccount account, DateTimeOffset nowUtc)
    {
        if (account.AutoRetryAfter is null || account.AutoRetryAfter <= nowUtc)
        {
            return null;
        }

        var reason = string.IsNullOrWhiteSpace(account.LastRiskReasonCode)
            ? SteamReasonCodes.Unknown
            : account.LastRiskReasonCode;
        return $"Повышенный риск ({reason}). Авто-повторы безопаснее запускать после {account.AutoRetryAfter:O}.";
    }

    private TimeSpan GetOperationDelay(SteamAccount account, DateTimeOffset nowUtc)
    {
        if (_minSensitiveIntervalSeconds <= 0 || account.LastSensitiveOpAt is null)
        {
            return TimeSpan.Zero;
        }

        var jitterSeconds = Math.Abs(account.Id.GetHashCode()) % 3;
        var notBefore = account.LastSensitiveOpAt.Value
            .AddSeconds(_minSensitiveIntervalSeconds)
            .AddSeconds(jitterSeconds);
        return notBefore > nowUtc ? notBefore - nowUtc : TimeSpan.Zero;
    }

    private TimeSpan GetCooldownDuration(int streak)
    {
        // Escalates from configured base minutes to +50% for repeated failures.
        var multiplier = streak >= 3 ? 1.5 : 1.0;
        return TimeSpan.FromMinutes(_autoRetryCooldownMinutes * multiplier);
    }

    private static bool IsHardCooldownReason(string reasonCode)
    {
        return string.Equals(reasonCode, SteamReasonCodes.AntiBotBlocked, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.AccessDenied, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.AuthThrottled, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.SessionReplaced, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.CooldownActive, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSoftRiskReason(string reasonCode)
    {
        return string.Equals(reasonCode, SteamReasonCodes.Timeout, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.AuthSessionMissing, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthFailure(string reasonCode)
    {
        return string.Equals(reasonCode, SteamReasonCodes.AuthSessionMissing, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.AccessDenied, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasonCode, SteamReasonCodes.InvalidCredentials, StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(
        IConfiguration? configuration,
        string key,
        int fallback,
        int min,
        int max)
    {
        if (configuration is null)
        {
            return fallback;
        }

        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }
}
