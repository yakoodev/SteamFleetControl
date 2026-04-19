using SteamFleet.Contracts.Enums;

namespace SteamFleet.Contracts.Accounts;

public sealed class AccountDto
{
    public Guid Id { get; set; }
    public required string LoginName { get; set; }
    public string? DisplayName { get; set; }
    public string? SteamId64 { get; set; }
    public string? ProfileUrl { get; set; }
    public string? Email { get; set; }
    public string? PhoneMasked { get; set; }
    public string? Note { get; set; }
    public string? Proxy { get; set; }
    public Guid? FolderId { get; set; }
    public string? FolderName { get; set; }
    public bool IsExternal { get; set; }
    public string? ExternalSource { get; set; }
    public string? SteamFamilyId { get; set; }
    public string? SteamFamilyRole { get; set; }
    public bool IsFamilyOrganizer { get; set; }
    public DateTimeOffset? FamilySyncedAt { get; set; }
    public int FamilyAccountsCount { get; set; }
    public AccountRiskLevel RiskLevel { get; set; } = AccountRiskLevel.Normal;
    public int AuthFailStreak { get; set; }
    public int RiskSignalStreak { get; set; }
    public string? LastRiskReasonCode { get; set; }
    public DateTimeOffset? LastRiskAt { get; set; }
    public DateTimeOffset? AutoRetryAfter { get; set; }
    public DateTimeOffset? LastSensitiveOpAt { get; set; }
    public AccountStatus Status { get; set; }
    public DateTimeOffset? GamesLastSyncAt { get; set; }
    public int GamesCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Tags { get; set; } = [];
}
