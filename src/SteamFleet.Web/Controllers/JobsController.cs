using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;
using SteamFleet.Web.Models.Forms;

namespace SteamFleet.Web.Controllers;

[Authorize]
[Route("jobs")]
public sealed class JobsController(IJobService jobService, IBackgroundJobClient backgroundJobs, IAccountService accountService) : AppControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var jobs = await jobService.GetRecentAsync(100, cancellationToken);
        return View(jobs);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [HttpGet("create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var model = new CreateJobFormModel();
        await PopulateOptionsAsync(model, cancellationToken);
        return View(model);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("create")]
    public async Task<IActionResult> CreatePost([FromForm] CreateJobFormModel model, CancellationToken cancellationToken)
    {
        var requiresSelectedAccounts = model.Type is not JobType.FriendsAddByInvite and not JobType.FriendsConnectFamilyMain;
        if (requiresSelectedAccounts && model.SelectedAccountIds.Count == 0)
        {
            ModelState.AddModelError(nameof(model.SelectedAccountIds), "Нужно выбрать хотя бы один аккаунт");
        }

        if (model.Type == JobType.FriendsAddByInvite &&
            !model.FriendsPairs.Any(x => x.SourceAccountId is not null && x.TargetAccountId is not null && x.SourceAccountId != x.TargetAccountId))
        {
            ModelState.AddModelError(nameof(model.FriendsPairs), "Добавьте хотя бы одну корректную пару source -> target.");
        }

        if (model.Type == JobType.FriendsConnectFamilyMain && model.FamilyMainAccountId is null)
        {
            ModelState.AddModelError(nameof(model.FamilyMainAccountId), "Выберите главный аккаунт семейной группы.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync(model, cancellationToken);
            return View("Create", model);
        }

        foreach (var (key, value) in BuildTypedPayload(model))
        {
            model.Payload[key] = value;
        }

        var accountIds = model.Type switch
        {
            JobType.FriendsAddByInvite => model.FriendsPairs
                .Where(x => x.SourceAccountId is not null)
                .Select(x => x.SourceAccountId!.Value)
                .Distinct()
                .ToList(),
            JobType.FriendsConnectFamilyMain when model.FamilyMainAccountId is not null =>
            [
                model.FamilyMainAccountId.Value
            ],
            _ => model.SelectedAccountIds
        };

        var request = new JobCreateRequest
        {
            Type = model.Type,
            AccountIds = accountIds,
            DryRun = model.DryRun,
            Parallelism = model.Parallelism,
            RetryCount = model.RetryCount,
            Payload = model.Payload
        };

        var job = await jobService.CreateAsync(request, ActorId, ClientIp, cancellationToken);
        backgroundJobs.Enqueue<HangfireJobExecutor>(x => x.ExecuteAsync(job.Id, CancellationToken.None));
        TempData["Success"] = "Задача поставлена в очередь";
        return RedirectToAction(nameof(Details), new { id = job.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobService.GetByIdAsync(id, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        var items = await jobService.GetItemsAsync(id, cancellationToken);
        var model = new JobDetailsViewModel
        {
            Job = job,
            Items = items
        };

        return View(model);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        await jobService.CancelAsync(id, ActorId, ClientIp, cancellationToken);
        TempData["Success"] = "Задача отменена";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpGet("{id:guid}/sensitive-report")]
    public async Task<IActionResult> SensitiveReport(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var csv = await jobService.DownloadSensitiveReportOnceAsync(id, ActorId, ClientIp, cancellationToken);
            if (csv is null)
            {
                TempData["Error"] = "Чувствительный отчёт недоступен.";
                return RedirectToAction(nameof(Details), new { id });
            }

            return File(csv, "text/csv", $"job-sensitive-report-{id}.csv");
        }
        catch (InvalidOperationException)
        {
            TempData["Error"] = "Отчёт уже был скачан ранее.";
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    private static Dictionary<string, string> BuildTypedPayload(CreateJobFormModel model)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        switch (model.Type)
        {
            case JobType.ProfileUpdate:
                AddIfNotEmpty(payload, "displayName", model.DisplayName);
                AddIfNotEmpty(payload, "summary", model.Summary);
                AddIfNotEmpty(payload, "realName", model.RealName);
                AddIfNotEmpty(payload, "country", model.Country);
                AddIfNotEmpty(payload, "state", model.State);
                AddIfNotEmpty(payload, "city", model.City);
                AddIfNotEmpty(payload, "customUrl", model.CustomUrl);
                break;
            case JobType.PrivacyUpdate:
                if (model.ProfilePrivate is not null) payload["profilePrivate"] = model.ProfilePrivate.Value.ToString();
                if (model.FriendsPrivate is not null) payload["friendsPrivate"] = model.FriendsPrivate.Value.ToString();
                if (model.InventoryPrivate is not null) payload["inventoryPrivate"] = model.InventoryPrivate.Value.ToString();
                break;
            case JobType.AvatarUpdate:
                AddIfNotEmpty(payload, "avatarBase64", model.AvatarBase64);
                break;
            case JobType.TagsAssign:
                AddIfNotEmpty(payload, "tags", NormalizePipeSeparated(model.TagsRaw));
                break;
            case JobType.GroupMove:
                AddIfNotEmpty(payload, "folder", model.FolderName);
                break;
            case JobType.AddNote:
                AddIfNotEmpty(payload, "note", model.NoteText);
                break;
            case JobType.PasswordChange:
                AddIfNotEmpty(payload, "newPassword", model.FixedNewPassword);
                payload["generateLength"] = Math.Clamp(model.GeneratePasswordLength, 12, 64).ToString();
                payload["deauthorizeAfterChange"] = model.DeauthorizeAfterChange.ToString();
                break;
            case JobType.SessionsDeauthorize:
            case JobType.SessionValidate:
            case JobType.SessionRefresh:
                break;
            case JobType.FriendsAddByInvite:
                var pairs = model.FriendsPairs
                    .Where(x => x.SourceAccountId is not null && x.TargetAccountId is not null && x.SourceAccountId != x.TargetAccountId)
                    .Select(x => $"{x.SourceAccountId}:{x.TargetAccountId}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (pairs.Length > 0)
                {
                    payload["pairs"] = string.Join(';', pairs);
                }

                break;
            case JobType.FriendsConnectFamilyMain:
                if (model.FamilyMainAccountId is not null)
                {
                    payload["mainAccountId"] = model.FamilyMainAccountId.Value.ToString();
                }
                break;
        }

        return payload;
    }

    private async Task PopulateOptionsAsync(CreateJobFormModel model, CancellationToken cancellationToken)
    {
        var accounts = await accountService.GetAsync(new Contracts.Accounts.AccountFilterRequest
        {
            Page = 1,
            PageSize = 5000
        }, cancellationToken);

        model.AvailableAccounts = accounts.Items
            .Select(x => new CreateJobFormModel.AccountOption(x.Id, x.LoginName, x.DisplayName, x.Status.ToString()))
            .ToList();

        model.MainAccountOptions = accounts.Items
            .Where(x => !x.IsExternal && !string.IsNullOrWhiteSpace(x.SteamFamilyId))
            .OrderBy(x => x.LoginName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new CreateJobFormModel.AccountOption(x.Id, x.LoginName, x.DisplayName, x.Status.ToString()))
            .ToList();

        if (model.FriendsPairs.Count == 0)
        {
            model.FriendsPairs.Add(new CreateJobFormModel.FriendPairFormItem());
        }
    }

    private static void AddIfNotEmpty(Dictionary<string, string> payload, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        payload[key] = value.Trim();
    }

    private static string NormalizePipeSeparated(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var tokens = value
            .Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join('|', tokens);
    }
}
