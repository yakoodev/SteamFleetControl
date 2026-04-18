using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Identity;

namespace SteamFleet.Persistence.Services;

public sealed class AdminBootstrapService(
    RoleManager<AppRole> roleManager,
    UserManager<AppUser> userManager,
    IConfiguration configuration,
    ILogger<AdminBootstrapService> logger) : IAdminBootstrapService
{
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new AppRole { Name = role, NormalizedName = role.ToUpperInvariant() });
            }
        }

        var email = configuration["ADMIN_EMAIL"];
        var password = configuration["ADMIN_PASSWORD"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("ADMIN_EMAIL/ADMIN_PASSWORD are not configured. SuperAdmin bootstrap skipped.");
            return;
        }

        var user = await userManager.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (user is null)
        {
            user = new AppUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Failed to bootstrap SuperAdmin: {string.Join(';', result.Errors.Select(e => e.Description))}");
            }

            await userManager.AddToRoleAsync(user, Roles.SuperAdmin);
            logger.LogInformation("Bootstrap SuperAdmin user created: {Email}", email);
            return;
        }

        if (!await userManager.IsInRoleAsync(user, Roles.SuperAdmin))
        {
            await userManager.AddToRoleAsync(user, Roles.SuperAdmin);
            logger.LogInformation("Existing user promoted to SuperAdmin: {Email}", email);
        }
    }
}
