using SteamFleet.Contracts.Enums;

namespace SteamFleet.Domain.Entities;

public sealed class FleetJob : EntityBase
{
    public JobType Type { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string PayloadJson { get; set; } = "{}";
    public string? CreatedBy { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public bool DryRun { get; set; }
    public int Parallelism { get; set; } = 5;
    public int RetryCount { get; set; } = 2;
    public JobSensitiveReport? SensitiveReport { get; set; }
    public List<FleetJobItem> Items { get; set; } = [];
}
