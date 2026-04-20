using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;
using SteamFleet.Web.Models.Forms;
using System.Net.Http.Headers;

namespace SteamFleet.Web.Controllers;

[Authorize]
[Route("jobs")]
public sealed class JobsController(IJobService jobService, IBackgroundJobClient backgroundJobs, IAccountService accountService) : AppControllerBase
{
    private const int MaxAvatarBytes = 2 * 1024 * 1024;
    private static readonly HttpClient AvatarDownloadClient = CreateAvatarHttpClient();

    [HttpGet("")]
    public async Task<IActionResult> Index(
        JobType? type,
        JobStatus? status,
        string? query,
        CancellationToken cancellationToken)
    {
        var jobs = await jobService.GetRecentAsync(100, cancellationToken);
        var filtered = jobs.AsEnumerable();
        if (type is not null)
        {
            filtered = filtered.Where(x => x.Type == type.Value);
        }

        if (status is not null)
        {
            filtered = filtered.Where(x => x.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = query.Trim();
            filtered = filtered.Where(x =>
                x.Id.ToString().Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                (x.CreatedBy?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return View(new JobsIndexViewModel
        {
            Type = type,
            Status = status,
            Query = query?.Trim(),
            Jobs = filtered.ToList()
        });
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
        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync(model, cancellationToken);
            return View("Create", model);
        }

        string? avatarBase64 = null;
        if (model.Type == JobType.AvatarUpdate)
        {
            var avatarResolution = await ResolveAvatarBase64Async(model, cancellationToken);
            if (!avatarResolution.Success)
            {
                ModelState.AddModelError(nameof(model.AvatarFile), avatarResolution.ErrorMessage ?? "Не удалось обработать аватар.");
            }
            else
            {
                avatarBase64 = avatarResolution.Base64;
            }
        }

        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync(model, cancellationToken);
            return View("Create", model);
        }

        model.Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in BuildTypedPayload(model, avatarBase64))
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
        var accounts = await LoadAccountLookupAsync(
            items.Select(x => x.AccountId).Distinct().ToArray(),
            cancellationToken);

        var model = new JobDetailsViewModel
        {
            Job = job,
            Items = items,
            Accounts = accounts
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

    private static Dictionary<string, string> BuildTypedPayload(CreateJobFormModel model, string? avatarBase64)
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
                AddIfNotEmpty(payload, "avatarBase64", avatarBase64 ?? model.AvatarBase64);
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

    private async Task<IReadOnlyDictionary<Guid, JobDetailsViewModel.JobAccountInfo>> LoadAccountLookupAsync(
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return new Dictionary<Guid, JobDetailsViewModel.JobAccountInfo>();
        }

        var wanted = new HashSet<Guid>(accountIds);
        var result = new Dictionary<Guid, JobDetailsViewModel.JobAccountInfo>();
        const int pageSize = 500;
        var page = 1;

        while (result.Count < wanted.Count)
        {
            var pageResult = await accountService.GetAsync(new AccountFilterRequest
            {
                Page = page,
                PageSize = pageSize
            }, cancellationToken);

            if (pageResult.Items.Count == 0)
            {
                break;
            }

            foreach (var account in pageResult.Items)
            {
                if (!wanted.Contains(account.Id))
                {
                    continue;
                }

                result[account.Id] = new JobDetailsViewModel.JobAccountInfo(
                    account.LoginName,
                    account.DisplayName,
                    account.Status.ToString());
            }

            if (page * pageSize >= pageResult.TotalCount)
            {
                break;
            }

            page++;
        }

        return result;
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

    private static HttpClient CreateAvatarHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamFleetControl/1.0 JobsAvatarResolver");
        return client;
    }

    private async Task<AvatarResolutionResult> ResolveAvatarBase64Async(CreateJobFormModel model, CancellationToken cancellationToken)
    {
        if (model.AvatarFile is { Length: > 0 } uploadedFile)
        {
            return await ReadUploadedAvatarAsync(uploadedFile, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(model.AvatarUrl))
        {
            return await DownloadAvatarFromUrlAsync(model.AvatarUrl, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(model.AvatarBase64))
        {
            try
            {
                var raw = Convert.FromBase64String(model.AvatarBase64.Trim());
                if (raw.Length == 0)
                {
                    return AvatarResolutionResult.Fail("Передан пустой avatarBase64.");
                }

                if (raw.Length > MaxAvatarBytes)
                {
                    return AvatarResolutionResult.Fail($"Размер аватара превышает {MaxAvatarBytes / 1024 / 1024} МБ.");
                }

                return AvatarResolutionResult.Ok(model.AvatarBase64.Trim());
            }
            catch (FormatException)
            {
                return AvatarResolutionResult.Fail("avatarBase64 имеет некорректный формат.");
            }
        }

        return AvatarResolutionResult.Fail("Укажите файл аватара или ссылку на изображение.");
    }

    private static async Task<AvatarResolutionResult> ReadUploadedAvatarAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (!LooksLikeImageContentType(file.ContentType))
        {
            return AvatarResolutionResult.Fail("Загруженный файл не похож на изображение.");
        }

        if (file.Length <= 0)
        {
            return AvatarResolutionResult.Fail("Файл аватара пуст.");
        }

        if (file.Length > MaxAvatarBytes)
        {
            return AvatarResolutionResult.Fail($"Размер файла превышает {MaxAvatarBytes / 1024 / 1024} МБ.");
        }

        await using var stream = file.OpenReadStream();
        var bytes = await ReadBytesWithLimitAsync(stream, MaxAvatarBytes, cancellationToken);
        if (bytes is null || bytes.Length == 0)
        {
            return AvatarResolutionResult.Fail("Не удалось прочитать файл аватара.");
        }

        return AvatarResolutionResult.Ok(Convert.ToBase64String(bytes));
    }

    private static async Task<AvatarResolutionResult> DownloadAvatarFromUrlAsync(string rawUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return AvatarResolutionResult.Fail("Ссылка на аватар должна быть абсолютным URL с http/https.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await AvatarDownloadClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return AvatarResolutionResult.Fail($"Не удалось скачать изображение: HTTP {(int)response.StatusCode}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!LooksLikeImageContentType(mediaType))
            {
                return AvatarResolutionResult.Fail("URL не указывает на изображение (Content-Type не image/*).");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxAvatarBytes)
            {
                return AvatarResolutionResult.Fail($"Размер изображения по ссылке превышает {MaxAvatarBytes / 1024 / 1024} МБ.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadBytesWithLimitAsync(stream, MaxAvatarBytes, cancellationToken);
            if (bytes is null || bytes.Length == 0)
            {
                return AvatarResolutionResult.Fail("Изображение по ссылке пустое или не удалось его прочитать.");
            }

            return AvatarResolutionResult.Ok(Convert.ToBase64String(bytes));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AvatarResolutionResult.Fail("Скачивание аватара превысило таймаут.");
        }
        catch (HttpRequestException ex)
        {
            return AvatarResolutionResult.Fail($"Ошибка загрузки изображения: {ex.Message}");
        }
    }

    private static async Task<byte[]?> ReadBytesWithLimitAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        await using var memory = new MemoryStream();
        var buffer = new byte[81920];
        var totalRead = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > maxBytes)
            {
                return null;
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return memory.ToArray();
    }

    private static bool LooksLikeImageContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return parsed.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        }

        return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct AvatarResolutionResult(bool Success, string? Base64, string? ErrorMessage)
    {
        public static AvatarResolutionResult Ok(string base64) => new(true, base64, null);
        public static AvatarResolutionResult Fail(string error) => new(false, null, error);
    }
}
