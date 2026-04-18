namespace SteamFleet.Contracts.Accounts;

public sealed class FriendInviteLinkDto
{
    public Guid AccountId { get; set; }
    public string? InviteUrl { get; set; }
    public string? InviteCode { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
}
