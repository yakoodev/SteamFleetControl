namespace SteamFleet.Contracts.Accounts;

public sealed class AccountPasswordChangeRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
    public bool GenerateIfEmpty { get; set; } = true;
    public bool DeauthorizeAfterChange { get; set; }
    public string? ConfirmationCode { get; set; }
    public string? ConfirmationRequestId { get; set; }
}
