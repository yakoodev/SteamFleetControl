namespace SteamFleet.Contracts.Accounts;

public sealed class AccountGameDto
{
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int PlaytimeMinutes { get; set; }
    public string? ImgIconUrl { get; set; }
    public Guid SourceAccountId { get; set; }
    public string SourceLoginName { get; set; } = string.Empty;
    public AccountGameAvailability Availability { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}
