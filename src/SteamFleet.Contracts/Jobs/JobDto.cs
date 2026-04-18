using SteamFleet.Contracts.Enums;

namespace SteamFleet.Contracts.Jobs;

public sealed class JobDto
{
    public Guid Id { get; set; }
    public JobType Type { get; set; }
    public JobStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? CreatedBy { get; set; }
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public bool DryRun { get; set; }
    public bool HasSensitiveReport { get; set; }
    public bool SensitiveReportConsumed { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
