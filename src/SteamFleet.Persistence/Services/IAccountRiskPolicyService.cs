using SteamFleet.Contracts.Enums;
using SteamFleet.Domain.Entities;

namespace SteamFleet.Persistence.Services;

public sealed record AccountRiskPrecheck(
    bool IsBlocked,
    string? ReasonCode,
    bool Retryable,
    DateTimeOffset? RetryAfter,
    TimeSpan Delay);

public sealed record AccountRiskTransition(
    AccountRiskLevel PreviousLevel,
    AccountRiskLevel CurrentLevel,
    bool CooldownScheduled,
    bool Recovered);

public interface IAccountRiskPolicyService
{
    AccountRiskPrecheck EvaluateBeforeOperation(SteamAccount account, bool automated, DateTimeOffset nowUtc);
    void MarkOperationStarted(SteamAccount account, DateTimeOffset nowUtc);
    AccountRiskTransition MarkOperationSuccess(SteamAccount account, DateTimeOffset nowUtc);
    AccountRiskTransition MarkOperationFailure(SteamAccount account, string? reasonCode, bool automated, DateTimeOffset nowUtc);
    string? BuildManualWarning(SteamAccount account, DateTimeOffset nowUtc);
}
