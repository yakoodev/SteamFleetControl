using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Services;

namespace SteamFleet.UnitTests;

public sealed class RiskPolicyAndLockTests
{
    [Fact]
    public void EvaluateBeforeOperation_AutomatedCooldown_Blocks()
    {
        var service = new AccountRiskPolicyService();
        var now = DateTimeOffset.UtcNow;
        var account = new SteamAccount
        {
            LoginName = "risk-user",
            Status = AccountStatus.Active,
            AutoRetryAfter = now.AddMinutes(5),
            RiskLevel = AccountRiskLevel.Cooldown
        };

        var precheck = service.EvaluateBeforeOperation(account, automated: true, now);

        Assert.True(precheck.IsBlocked);
        Assert.Equal(SteamReasonCodes.CooldownActive, precheck.ReasonCode);
        Assert.True(precheck.Retryable);
        Assert.NotNull(precheck.RetryAfter);
    }

    [Fact]
    public void MarkOperationFailure_AccessDenied_SchedulesCooldown()
    {
        var service = new AccountRiskPolicyService();
        var account = new SteamAccount
        {
            LoginName = "risk-user",
            Status = AccountStatus.Active
        };

        var transition = service.MarkOperationFailure(
            account,
            SteamReasonCodes.AccessDenied,
            automated: true,
            DateTimeOffset.UtcNow);

        Assert.Equal(AccountRiskLevel.Cooldown, account.RiskLevel);
        Assert.True(transition.CooldownScheduled);
        Assert.Equal(SteamReasonCodes.AccessDenied, account.LastRiskReasonCode);
        Assert.NotNull(account.AutoRetryAfter);
    }

    [Fact]
    public void MarkOperationSuccess_ResetsRiskState_AndMarksRecovered()
    {
        var service = new AccountRiskPolicyService();
        var account = new SteamAccount
        {
            LoginName = "risk-user",
            Status = AccountStatus.Active,
            RiskLevel = AccountRiskLevel.Elevated,
            AuthFailStreak = 2,
            RiskSignalStreak = 3,
            LastRiskReasonCode = SteamReasonCodes.Timeout,
            LastRiskAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            AutoRetryAfter = DateTimeOffset.UtcNow.AddMinutes(10)
        };

        var transition = service.MarkOperationSuccess(account, DateTimeOffset.UtcNow);

        Assert.True(transition.Recovered);
        Assert.Equal(AccountRiskLevel.Normal, account.RiskLevel);
        Assert.Equal(0, account.AuthFailStreak);
        Assert.Equal(0, account.RiskSignalStreak);
        Assert.Null(account.LastRiskReasonCode);
        Assert.Null(account.LastRiskAt);
        Assert.Null(account.AutoRetryAfter);
    }

    [Fact]
    public async Task InMemoryAccountOperationLock_SerializesConcurrentAccess()
    {
        var locker = new InMemoryAccountOperationLock();
        var accountId = Guid.NewGuid();
        var inSection = 0;
        var maxParallel = 0;

        async Task RunAsync()
        {
            await using var lease = await locker.AcquireAsync(accountId);
            var current = Interlocked.Increment(ref inSection);
            maxParallel = Math.Max(maxParallel, current);
            await Task.Delay(50);
            Interlocked.Decrement(ref inSection);
        }

        await Task.WhenAll(RunAsync(), RunAsync(), RunAsync());
        Assert.Equal(1, maxParallel);
    }

}
