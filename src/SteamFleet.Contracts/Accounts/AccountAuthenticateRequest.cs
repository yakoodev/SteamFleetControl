namespace SteamFleet.Contracts.Accounts;

public sealed class AccountAuthenticateRequest
{
    public string? Password { get; set; }
    public string? SharedSecret { get; set; }
    public string? IdentitySecret { get; set; }
    public string? GuardCode { get; set; }
    public bool AllowDeviceConfirmation { get; set; }
}
