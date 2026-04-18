using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;

namespace SteamFleet.Web.Controllers;

[Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Auditor)]
[Route("audit")]
public sealed class AuditController(IAuditService auditService) : AppControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] int skip = 0, [FromQuery] int take = 200, CancellationToken cancellationToken = default)
    {
        var eventsData = await auditService.GetAsync(skip, take, cancellationToken);
        return View(eventsData);
    }
}
