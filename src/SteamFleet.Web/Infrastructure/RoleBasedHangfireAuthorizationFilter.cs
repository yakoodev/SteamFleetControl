using Hangfire.Dashboard;

namespace SteamFleet.Web.Infrastructure;

public sealed class RoleBasedHangfireAuthorizationFilter(params string[] roles) : IDashboardAuthorizationFilter
{
    private readonly HashSet<string> _roles = new(roles, StringComparer.OrdinalIgnoreCase);

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return _roles.Count == 0 || _roles.Any(httpContext.User.IsInRole);
    }
}
