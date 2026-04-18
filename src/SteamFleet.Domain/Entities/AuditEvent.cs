using SteamFleet.Contracts.Enums;

namespace SteamFleet.Domain.Entities;

public sealed class AuditEvent : EntityBase
{
    public AuditEventType EventType { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? ActorId { get; set; }
    public string? Ip { get; set; }
    public string PayloadJson { get; set; } = "{}";
}
