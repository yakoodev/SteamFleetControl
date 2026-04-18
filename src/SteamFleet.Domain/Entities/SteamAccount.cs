using SteamFleet.Contracts.Enums;

namespace SteamFleet.Domain.Entities;

public sealed class SteamAccount : EntityBase
{
    public required string LoginName { get; set; }
    public string? DisplayName { get; set; }
    public string? SteamId64 { get; set; }
    public string? ProfileUrl { get; set; }
    public string? Email { get; set; }
    public string? PhoneMasked { get; set; }
    public Guid? FolderId { get; set; }
    public Folder? Folder { get; set; }
    public Guid? ParentAccountId { get; set; }
    public SteamAccount? ParentAccount { get; set; }
    public List<SteamAccount> ChildAccounts { get; set; } = [];
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public DateTimeOffset? GamesLastSyncAt { get; set; }
    public int GamesCount { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public string? Note { get; set; }
    public string? Proxy { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public SteamAccountSecret? Secret { get; set; }
    public List<SteamAccountTagLink> TagLinks { get; set; } = [];
    public List<SteamAccountGame> Games { get; set; } = [];
    public List<FleetJobItem> JobItems { get; set; } = [];
}
