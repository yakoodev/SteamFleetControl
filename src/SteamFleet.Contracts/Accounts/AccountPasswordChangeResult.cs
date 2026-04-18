namespace SteamFleet.Contracts.Accounts;

public sealed class AccountPasswordChangeResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReasonCode { get; set; }
    public bool Retryable { get; set; }
    public string? NewPassword { get; set; }
    public bool Deauthorized { get; set; }
    public bool RequiresConfirmation { get; set; }
    public string? ConfirmationRequestId { get; set; }
    public DateTimeOffset? ConfirmationExpiresAt { get; set; }
}
