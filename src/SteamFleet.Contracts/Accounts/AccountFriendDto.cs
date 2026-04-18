namespace SteamFleet.Contracts.Accounts;

public sealed class AccountFriendDto
{
    public string SteamId64 { get; set; } = string.Empty;
    public string? PersonaName { get; set; }
    public string? ProfileUrl { get; set; }
}
