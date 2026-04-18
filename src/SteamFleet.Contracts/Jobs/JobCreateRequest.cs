using SteamFleet.Contracts.Enums;

namespace SteamFleet.Contracts.Jobs;

public sealed class JobCreateRequest
{
    public JobType Type { get; set; }
    public List<Guid> AccountIds { get; set; } = [];
    public bool DryRun { get; set; }
    public int Parallelism { get; set; } = 5;
    public int RetryCount { get; set; } = 2;
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
