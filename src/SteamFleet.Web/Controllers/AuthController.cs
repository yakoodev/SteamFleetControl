using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Auth;
using SteamFleet.Persistence.Identity;

namespace SteamFleet.Web.Controllers;

[AllowAnonymous]
[Route("auth")]
public sealed class AuthController(SignInManager<AppUser> signInManager) : AppControllerBase
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<IActionResult> LoginPost([FromForm] LoginRequest request, [FromQuery] string? returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            ViewData["ReturnUrl"] = returnUrl;
            return View("Login", request);
        }

        var result = await signInManager.PasswordSignInAsync(request.Email, request.Password, request.RememberMe, lockoutOnFailure: false);
        if (result.Succeeded)
        {
            return Redirect(returnUrl ?? Url.Action("Index", "Accounts")!);
        }

        ModelState.AddModelError(string.Empty, "Неверный email или пароль.");
        ViewData["ReturnUrl"] = returnUrl;
        return View("Login", request);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("denied")]
    public IActionResult Denied()
    {
        return View();
    }
}
