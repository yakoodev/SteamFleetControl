namespace SteamFleet.Contracts.Accounts;

public sealed class AcceptInviteRequest
{
    public Guid? TargetAccountId { get; set; }
    public Guid? SourceAccountId { get; set; }
    public string InviteUrl { get; set; } = string.Empty;
}
