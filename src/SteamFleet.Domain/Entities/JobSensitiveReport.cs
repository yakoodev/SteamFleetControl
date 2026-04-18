namespace SteamFleet.Domain.Entities;

public sealed class JobSensitiveReport : EntityBase
{
    public Guid JobId { get; set; }
    public FleetJob? Job { get; set; }
    public string EncryptedPayload { get; set; } = string.Empty;
    public string EncryptionVersion { get; set; } = string.Empty;
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? ConsumedBy { get; set; }
}
