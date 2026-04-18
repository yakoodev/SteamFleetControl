using SteamFleet.Contracts.Enums;

namespace SteamFleet.Contracts.Jobs;

public sealed class JobItemDto
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid AccountId { get; set; }
    public JobItemStatus Status { get; set; }
    public int Attempt { get; set; }
    public string? ErrorText { get; set; }
    public string? ReasonCode { get; set; }
    public bool Retryable { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public Dictionary<string, string> Request { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Result { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
