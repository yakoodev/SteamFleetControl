using SteamFleet.Contracts.Audit;
using SteamFleet.Contracts.Enums;

namespace SteamFleet.Persistence.Services;

public interface IAuditService
{
    Task WriteAsync(
        AuditEventType eventType,
        string entityType,
        string entityId,
        string? actorId,
        string? ip,
        Dictionary<string, string>? payload = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AuditEventDto>> GetAsync(int skip, int take, CancellationToken cancellationToken = default);
    Task<AuditEventDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
