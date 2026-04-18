namespace SteamFleet.Contracts.Jobs;

public sealed class FriendsAddByInviteJobPayload
{
    public List<FriendsInvitePair> Pairs { get; set; } = [];
    public bool DryRun { get; set; }
}

public sealed class FriendsInvitePair
{
    public Guid SourceAccountId { get; set; }
    public Guid TargetAccountId { get; set; }
}
