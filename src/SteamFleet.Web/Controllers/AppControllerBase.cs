using Microsoft.AspNetCore.Mvc;

namespace SteamFleet.Web.Controllers;

public abstract class AppControllerBase : Controller
{
    protected string ActorId => User.Identity?.Name ?? "system";
    protected string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}
