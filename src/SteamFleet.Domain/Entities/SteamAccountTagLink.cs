namespace SteamFleet.Domain.Entities;

public sealed class SteamAccountTagLink
{
    public Guid AccountId { get; set; }
    public SteamAccount? Account { get; set; }
    public Guid TagId { get; set; }
    public SteamAccountTag? Tag { get; set; }
}
