using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Auth;
using SteamFleet.Persistence.Identity;

namespace SteamFleet.Web.Controllers.Api;

[ApiController]
[IgnoreAntiforgeryToken]
[Route("api/auth")]
public sealed class AuthApiController(SignInManager<AppUser> signInManager, UserManager<AppUser> userManager) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        var result = await signInManager.PasswordSignInAsync(request.Email, request.Password, request.RememberMe, lockoutOnFailure: false);
        if (!result.Succeeded)
        {
            return Unauthorized(new LoginResponse { Succeeded = false, Message = "Invalid credentials" });
        }

        return Ok(new LoginResponse { Succeeded = true, Message = "Authenticated" });
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<ActionResult<LoginResponse>> Logout()
    {
        await signInManager.SignOutAsync();
        return Ok(new LoginResponse { Succeeded = true, Message = "Logged out" });
    }

    [Authorize]
    [HttpPost("refresh")]
    public async Task<ActionResult<LoginResponse>> Refresh()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized(new LoginResponse { Succeeded = false, Message = "User not found" });
        }

        await signInManager.RefreshSignInAsync(user);
        return Ok(new LoginResponse { Succeeded = true, Message = "Session refreshed" });
    }

    [Authorize]
    [HttpPost("2fa/enable")]
    public ActionResult<LoginResponse> Enable2Fa()
    {
        return Ok(new LoginResponse { Succeeded = true, Message = "2FA endpoint is available for future extension" });
    }
}
