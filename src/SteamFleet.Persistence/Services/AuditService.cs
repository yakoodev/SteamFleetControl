using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Audit;
using SteamFleet.Contracts.Enums;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;

namespace SteamFleet.Persistence.Services;

public sealed class AuditService(SteamFleetDbContext dbContext) : IAuditService
{
    public async Task WriteAsync(
        AuditEventType eventType,
        string entityType,
        string entityId,
        string? actorId,
        string? ip,
        Dictionary<string, string>? payload = null,
        CancellationToken cancellationToken = default)
    {
        var entity = new AuditEvent
        {
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            ActorId = actorId,
            Ip = ip,
            PayloadJson = JsonSerialization.SerializeDictionary(payload)
        };

        await dbContext.AuditEvents.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<AuditEventDto>> GetAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 500);

        return await dbContext.AuditEvents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(x => new AuditEventDto
            {
                Id = x.Id,
                EventType = x.EventType,
                EntityType = x.EntityType,
                EntityId = x.EntityId,
                ActorId = x.ActorId,
                Ip = x.Ip,
                Payload = JsonSerialization.DeserializeDictionary(x.PayloadJson),
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<AuditEventDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await dbContext.AuditEvents
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new AuditEventDto
            {
                Id = x.Id,
                EventType = x.EventType,
                EntityType = x.EntityType,
                EntityId = x.EntityId,
                ActorId = x.ActorId,
                Ip = x.Ip,
                Payload = JsonSerialization.DeserializeDictionary(x.PayloadJson),
                CreatedAt = x.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
    }
}
