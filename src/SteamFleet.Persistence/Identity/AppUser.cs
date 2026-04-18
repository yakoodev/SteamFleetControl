using Microsoft.AspNetCore.Identity;

namespace SteamFleet.Persistence.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
