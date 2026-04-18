using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;

namespace SteamFleet.Web.Controllers.Api;

[ApiController]
[IgnoreAntiforgeryToken]
[Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Auditor)]
[Route("api/audit-events")]
public sealed class AuditApiController(IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyCollection<SteamFleet.Contracts.Audit.AuditEventDto>> Get([FromQuery] int skip = 0, [FromQuery] int take = 200, CancellationToken cancellationToken = default)
        => auditService.GetAsync(skip, take, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SteamFleet.Contracts.Audit.AuditEventDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await auditService.GetByIdAsync(id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }
}
