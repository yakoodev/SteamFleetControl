namespace SteamFleet.Domain.Entities;

public sealed class SteamAccountGame : EntityBase
{
    public Guid AccountId { get; set; }
    public SteamAccount? Account { get; set; }
    public int AppId { get; set; }
    public required string Name { get; set; }
    public int PlaytimeMinutes { get; set; }
    public string? ImgIconUrl { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}
