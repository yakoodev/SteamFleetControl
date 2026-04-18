namespace SteamFleet.Domain.Entities;

public sealed class Folder : EntityBase
{
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public Folder? Parent { get; set; }
    public List<Folder> Children { get; set; } = [];
    public List<SteamAccount> Accounts { get; set; } = [];
}
