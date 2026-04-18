using SteamFleet.Contracts.Enums;

namespace SteamFleet.Contracts.Audit;

public sealed class AuditEventDto
{
    public Guid Id { get; set; }
    public AuditEventType EventType { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? ActorId { get; set; }
    public string? Ip { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CreatedAt { get; set; }
}
