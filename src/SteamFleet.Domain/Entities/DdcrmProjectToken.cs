namespace SteamFleet.Domain.Entities;

public sealed class DdcrmProjectToken : EntityBase
{
    public Guid ProjectId { get; set; }
    public string TokenHashSha256 { get; set; } = string.Empty;
    public string ScopesCsv { get; set; } = "read,jobs";
    public string Status { get; set; } = "active";
}
