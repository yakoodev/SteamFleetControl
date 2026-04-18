namespace SteamFleet.Contracts.Jobs;

public sealed class FriendsConnectFamilyMainPayload
{
    public Guid MainAccountId { get; set; }
    public List<Guid> ChildAccountIds { get; set; } = [];
    public bool DryRun { get; set; }
}
