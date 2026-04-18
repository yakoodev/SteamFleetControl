namespace SteamFleet.Contracts.Accounts;

public sealed class AccountFriendsSnapshotDto
{
    public Guid AccountId { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public IReadOnlyCollection<AccountFriendDto> Friends { get; set; } = [];
}
