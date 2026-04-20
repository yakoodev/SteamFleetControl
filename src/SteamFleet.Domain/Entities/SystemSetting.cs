namespace SteamFleet.Domain.Entities;

public sealed class SystemSetting : EntityBase
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
}

