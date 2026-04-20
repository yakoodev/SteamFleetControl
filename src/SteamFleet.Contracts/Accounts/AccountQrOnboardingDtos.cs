namespace SteamFleet.Contracts.Accounts;

public enum AccountQrOnboardingStatus
{
    Pending,
    Completed,
    Conflict,
    Failed,
    Canceled,
    Expired
}

public sealed class AccountQrOnboardingStartResult
{
    public Guid FlowId { get; set; }
    public string ChallengeUrl { get; set; } = string.Empty;
    public string QrImageDataUrl { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int PollingIntervalSeconds { get; set; }
}

public sealed class AccountQrOnboardingExistingAccount
{
    public Guid Id { get; set; }
    public string LoginName { get; set; } = string.Empty;
    public string? SteamId64 { get; set; }
}

public sealed class AccountQrOnboardingPollResult
{
    public Guid FlowId { get; set; }
    public AccountQrOnboardingStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReasonCode { get; set; }
    public AccountDto? CreatedAccount { get; set; }
    public AccountQrOnboardingExistingAccount? ExistingAccount { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
