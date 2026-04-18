namespace SteamFleet.Domain.Entities;

public sealed class SteamAccountTag : EntityBase
{
    public required string Name { get; set; }
    public string Color { get; set; } = "#6c757d";
    public List<SteamAccountTagLink> AccountLinks { get; set; } = [];
}
