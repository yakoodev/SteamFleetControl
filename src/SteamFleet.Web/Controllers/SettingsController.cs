using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Settings;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;
using SteamFleet.Web.Models.Forms;

namespace SteamFleet.Web.Controllers;

[Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
[Route("settings")]
public sealed class SettingsController(IOperationalSettingsService operationalSettingsService) : AppControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var settings = await operationalSettingsService.GetAsync(cancellationToken);
        return View(MapToForm(settings));
    }

    [HttpPost("")]
    [EnableRateLimiting("sensitive")]
    public async Task<IActionResult> Save([FromForm] OperationalSettingsFormModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View("Index", model);
        }

        var updated = await operationalSettingsService.UpdateAsync(
            new UpdateOperationalSafetySettingsRequest
            {
                SafeModeEnabled = model.SafeModeEnabled,
                BlockManualSensitiveDuringCooldown = model.BlockManualSensitiveDuringCooldown,
                DefaultJobParallelism = model.DefaultJobParallelism,
                DefaultJobRetryCount = model.DefaultJobRetryCount,
                MaxSensitiveParallelism = model.MaxSensitiveParallelism,
                MaxSensitiveAccountsPerJob = model.MaxSensitiveAccountsPerJob
            },
            ActorId,
            ClientIp,
            cancellationToken);

        TempData["Success"] = "Операционные настройки сохранены.";
        return RedirectToAction(nameof(Index));
    }

    private static OperationalSettingsFormModel MapToForm(OperationalSafetySettingsDto settings)
    {
        return new OperationalSettingsFormModel
        {
            SafeModeEnabled = settings.SafeModeEnabled,
            BlockManualSensitiveDuringCooldown = settings.BlockManualSensitiveDuringCooldown,
            DefaultJobParallelism = settings.DefaultJobParallelism,
            DefaultJobRetryCount = settings.DefaultJobRetryCount,
            MaxSensitiveParallelism = settings.MaxSensitiveParallelism,
            MaxSensitiveAccountsPerJob = settings.MaxSensitiveAccountsPerJob,
            UpdatedAt = settings.UpdatedAt
        };
    }
}

