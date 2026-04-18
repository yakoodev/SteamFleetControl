namespace SteamFleet.Integrations.Steam.Options;

public sealed class SteamGatewayOptions
{
    public string DeviceFriendlyName { get; set; } = $"{Environment.MachineName} (SteamFleet)";
    public string WebsiteId { get; set; } = "Community";
    public int AuthTimeoutSeconds { get; set; } = 120;
    public int WebTimeoutSeconds { get; set; } = 30;
    public int QrFlowTtlSeconds { get; set; } = 180;
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamFleet/1.0";
}
