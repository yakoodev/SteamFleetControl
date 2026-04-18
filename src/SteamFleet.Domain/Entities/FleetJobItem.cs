using SteamFleet.Contracts.Enums;

namespace SteamFleet.Domain.Entities;

public sealed class FleetJobItem : EntityBase
{
    public Guid JobId { get; set; }
    public FleetJob? Job { get; set; }
    public Guid AccountId { get; set; }
    public SteamAccount? Account { get; set; }
    public JobItemStatus Status { get; set; } = JobItemStatus.Pending;
    public int Attempt { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public string? ErrorText { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
