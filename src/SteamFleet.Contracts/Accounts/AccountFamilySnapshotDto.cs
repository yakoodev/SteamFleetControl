namespace SteamFleet.Contracts.Accounts;

public sealed class AccountFamilySnapshotDto
{
    public Guid AccountId { get; set; }
    public string? SteamFamilyId { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public bool IsOrganizer { get; set; }
    public string? SelfRole { get; set; }
    public IReadOnlyCollection<AccountFamilyMemberDto> Members { get; set; } = [];
}

public sealed class AccountFamilyMemberDto
{
    public Guid? AccountId { get; set; }
    public string SteamId64 { get; set; } = string.Empty;
    public string LoginName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsExternal { get; set; }
    public string? ExternalSource { get; set; }
    public string? ProfileUrl { get; set; }
    public string? FamilyRole { get; set; }
    public bool IsOrganizer { get; set; }
    public int GamesCount { get; set; }
    public DateTimeOffset? GamesLastSyncAt { get; set; }
}
