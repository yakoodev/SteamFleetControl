namespace SteamFleet.Contracts.Accounts;

public sealed class FamilyInviteRequest
{
    public Guid TargetAccountId { get; set; }
    public bool InviteAsChild { get; set; } = true;
}
