using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Steam;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;

namespace SteamFleet.Web.Controllers;

[Authorize]
[Route("accounts")]
public sealed class AccountsController(IAccountService accountService) : AppControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] AccountFilterRequest request, CancellationToken cancellationToken)
    {
        var page = await accountService.GetAsync(request, cancellationToken);
        var mainAccounts = await accountService.GetAsync(new AccountFilterRequest
        {
            FamilyGroup = "main",
            Page = 1,
            PageSize = 5000
        }, cancellationToken);

        ViewData["Filter"] = request;
        ViewData["FamilyMainOptions"] = mainAccounts.Items
            .OrderBy(x => x.LoginName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(page);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [HttpGet("create")]
    public IActionResult Create()
    {
        return View(new AccountUpsertRequest { LoginName = string.Empty });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("create")]
    public async Task<IActionResult> CreatePost([FromForm] AccountUpsertRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View("Create", request);
        }

        NormalizeTags(request);
        var created = await accountService.CreateAsync(request, ActorId, ClientIp, cancellationToken);
        TempData["Success"] = "Аккаунт создан";
        return RedirectToAction(nameof(Details), new { id = created.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(
        Guid id,
        [FromQuery] string? scope = "all",
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var account = await accountService.GetByIdAsync(id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        var parsedScope = scope?.ToLowerInvariant() switch
        {
            "owned" => AccountGamesScope.Owned,
            "family" => AccountGamesScope.Family,
            _ => AccountGamesScope.All
        };

        var games = await accountService.GetGamesAsync(id, parsedScope, q, page, pageSize, cancellationToken);
        var allAccounts = await accountService.GetAsync(new AccountFilterRequest
        {
            Page = 1,
            PageSize = 5000
        }, cancellationToken);

        ViewData["ParentCandidates"] = allAccounts.Items
            .Where(x => x.Id != id && x.ParentAccountId is null)
            .OrderBy(x => x.LoginName)
            .ToList();
        ViewData["ChildAccounts"] = allAccounts.Items
            .Where(x => x.ParentAccountId == id)
            .OrderBy(x => x.LoginName)
            .ToList();
        ViewData["FriendSourceCandidates"] = allAccounts.Items
            .Where(x => x.Id != id)
            .OrderBy(x => x.LoginName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ViewData["GamesScope"] = scope?.ToLowerInvariant() switch
        {
            "owned" => "owned",
            "family" => "family",
            _ => "all"
        };
        ViewData["GamesQuery"] = q ?? string.Empty;
        ViewData["GamesPage"] = page;
        ViewData["GamesPageSize"] = pageSize;
        ViewData["Games"] = games;
        ViewData["FriendInviteLink"] = await accountService.GetFriendInviteLinkAsync(id, cancellationToken);
        ViewData["FriendsSnapshot"] = await accountService.GetFriendsAsync(id, cancellationToken);
        account.Metadata.TryGetValue("password.change.pending.requestId", out var pendingPasswordRequestId);
        account.Metadata.TryGetValue("password.change.pending.expiresAt", out var pendingPasswordExpiresAtRaw);
        var hasPendingPasswordConfirmation = !string.IsNullOrWhiteSpace(pendingPasswordRequestId);
        ViewData["PasswordChangePending"] = hasPendingPasswordConfirmation;
        ViewData["PasswordChangePendingRequestId"] = pendingPasswordRequestId;
        ViewData["PasswordChangePendingExpiresAt"] = pendingPasswordExpiresAtRaw;

        return View(account);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var account = await accountService.GetByIdAsync(id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        var model = new AccountUpsertRequest
        {
            LoginName = account.LoginName,
            DisplayName = account.DisplayName,
            Email = account.Email,
            PhoneMasked = account.PhoneMasked,
            FolderName = account.FolderName,
            Note = account.Note,
            Proxy = account.Proxy,
            Tags = account.Tags,
            Metadata = account.Metadata,
            Status = account.Status
        };

        ViewData["AccountId"] = id;
        return View(model);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/edit")]
    public async Task<IActionResult> EditPost(Guid id, [FromForm] AccountUpsertRequest request, CancellationToken cancellationToken)
    {
        NormalizeTags(request);
        var updated = await accountService.UpdateAsync(id, request, ActorId, ClientIp, cancellationToken);
        if (updated is null)
        {
            return NotFound();
        }

        TempData["Success"] = "Аккаунт обновлен";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        await accountService.ArchiveAsync(id, ActorId, ClientIp, cancellationToken);
        TempData["Success"] = "Аккаунт архивирован";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/authenticate")]
    public async Task<IActionResult> Authenticate(Guid id, [FromForm] AccountAuthenticateRequest request, CancellationToken cancellationToken)
    {
        var result = await accountService.AuthenticateAsync(id, request, ActorId, ClientIp, cancellationToken);
        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Steam-авторизация успешна, сессия сохранена."
            : $"Steam-авторизация не удалась: {result.ErrorMessage}";

        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/validate")]
    public async Task<IActionResult> ValidateSession(Guid id, CancellationToken cancellationToken)
    {
        var result = await accountService.ValidateSessionAsync(id, ActorId, ClientIp, cancellationToken);
        TempData["Success"] = result.IsValid ? "Сессия валидна" : $"Сессия невалидна: {result.Reason}";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/refresh")]
    public async Task<IActionResult> RefreshSession(Guid id, CancellationToken cancellationToken)
    {
        await accountService.RefreshSessionAsync(id, ActorId, ClientIp, cancellationToken);
        TempData["Success"] = "Сессия обновлена";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/password/change")]
    public async Task<IActionResult> ChangePassword(Guid id, [FromForm] AccountPasswordChangeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.ChangePasswordAsync(id, request, ActorId, ClientIp, cancellationToken);
            if (result.RequiresConfirmation)
            {
                TempData["PasswordConfirmationModal"] = "1";
                if (!string.IsNullOrWhiteSpace(result.ConfirmationRequestId))
                {
                    TempData["PasswordConfirmationRequestId"] = result.ConfirmationRequestId;
                }

                if (result.ConfirmationExpiresAt is not null)
                {
                    TempData["PasswordConfirmationExpiresAt"] = result.ConfirmationExpiresAt.Value.ToString("g");
                }

                TempData["Error"] = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Steam отправил код подтверждения на email. Введите код в модальном окне."
                    : $"Смена пароля ожидает подтверждения: {result.ErrorMessage}";
                return RedirectToAction(nameof(Details), new { id, tab = "security" });
            }

            if (!result.Success)
            {
                TempData["Error"] = $"Смена пароля не удалась: {result.ErrorMessage}";
                return RedirectToAction(nameof(Details), new { id });
            }

            var successMessage = result.NewPassword is not null
                ? $"Пароль изменен. Новый пароль: {result.NewPassword}"
                : "Пароль изменен.";

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                successMessage = $"{successMessage} {result.ErrorMessage}";
            }

            TempData["Success"] = successMessage;
        }
        catch (SteamGatewayOperationException ex)
        {
            TempData["Error"] = $"Смена пароля не удалась: {ex.Message} (код: {ex.ReasonCode ?? SteamReasonCodes.Unknown})";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = $"Смена пароля не удалась: {ex.Message}";
        }

        return RedirectToAction(nameof(Details), new { id, tab = "security" });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/sessions/deauthorize")]
    public async Task<IActionResult> DeauthorizeSessions(Guid id, CancellationToken cancellationToken)
    {
        var result = await accountService.DeauthorizeAllSessionsAsync(id, ActorId, ClientIp, cancellationToken);
        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Все сессии завершены. Аккаунт переведен в RequiresRelogin."
            : $"Не удалось завершить сессии: {result.ErrorMessage}";

        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/games/refresh")]
    public async Task<IActionResult> RefreshGames(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await accountService.RefreshGamesAsync(id, ActorId, ClientIp, cancellationToken);
            TempData["Success"] = "Кэш игр обновлён";
        }
        catch (SteamGatewayOperationException ex)
        {
            TempData["Error"] = $"Не удалось обновить игры: {ex.Message} (код: {ex.ReasonCode ?? SteamReasonCodes.Unknown})";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = $"Не удалось обновить игры: {ex.Message}";
        }

        return RedirectToAction(nameof(Details), new { id, scope = "all" });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/friends/invite-link/sync")]
    public async Task<IActionResult> SyncFriendInviteLink(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await accountService.SyncFriendInviteLinkAsync(id, ActorId, ClientIp, cancellationToken);
            TempData["Success"] = "Invite-link синхронизирован.";
        }
        catch (SteamGatewayOperationException ex)
        {
            TempData["Error"] = $"Не удалось синхронизировать invite-link: {ex.Message} (код: {ex.ReasonCode ?? SteamReasonCodes.Unknown})";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = $"Не удалось синхронизировать invite-link: {ex.Message}";
        }

        return RedirectToAction(nameof(Details), new { id, tab = "friends" });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/friends/accept-invite")]
    public async Task<IActionResult> AcceptFriendInvite(Guid id, [FromForm] AcceptInviteRequest request, CancellationToken cancellationToken)
    {
        var targetAccountId = request.TargetAccountId ?? id;
        if (targetAccountId != id)
        {
            TempData["Error"] = "targetAccountId должен совпадать с id аккаунта в URL.";
            return RedirectToAction(nameof(Details), new { id, tab = "friends" });
        }

        if (request.SourceAccountId == id)
        {
            TempData["Error"] = "sourceAccountId должен отличаться от целевого аккаунта.";
            return RedirectToAction(nameof(Details), new { id, tab = "friends" });
        }

        try
        {
            var inviteUrl = request.InviteUrl?.Trim();
            if (string.IsNullOrWhiteSpace(inviteUrl))
            {
                if (request.SourceAccountId is null)
                {
                    TempData["Error"] = "Укажите Invite URL или выберите аккаунт-источник.";
                    return RedirectToAction(nameof(Details), new { id, tab = "friends" });
                }

                var synced = await accountService.SyncFriendInviteLinkAsync(request.SourceAccountId.Value, ActorId, ClientIp, cancellationToken);
                inviteUrl = synced.InviteUrl;
            }

            var result = await accountService.AcceptFriendInviteAsync(id, inviteUrl!, ActorId, ClientIp, cancellationToken);
            TempData[result.Success ? "Success" : "Error"] = result.Success
                ? "Приглашение в друзья принято."
                : $"Не удалось принять приглашение: {result.ErrorMessage}";
        }
        catch (SteamGatewayOperationException ex)
        {
            TempData["Error"] = $"Не удалось принять приглашение: {ex.Message} (код: {ex.ReasonCode ?? SteamReasonCodes.Unknown})";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = $"Не удалось принять приглашение: {ex.Message}";
        }

        return RedirectToAction(nameof(Details), new { id, tab = "friends" });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/friends/refresh")]
    public async Task<IActionResult> RefreshFriends(Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await accountService.RefreshFriendsAsync(id, ActorId, ClientIp, cancellationToken);
        TempData["Success"] = $"Список друзей обновлён: {snapshot.Friends.Count}.";
        return RedirectToAction(nameof(Details), new { id, tab = "friends" });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/family/assign-parent")]
    public async Task<IActionResult> AssignParent(Guid id, [FromForm] FamilyAssignParentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await accountService.AssignParentAsync(id, request.ParentAccountId, ActorId, ClientIp, cancellationToken);
            if (updated is null)
            {
                return NotFound();
            }

            TempData["Success"] = "Главный аккаунт назначен.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/family/remove-parent")]
    public async Task<IActionResult> RemoveParent(Guid id, CancellationToken cancellationToken)
    {
        var updated = await accountService.RemoveParentAsync(id, ActorId, ClientIp, cancellationToken);
        if (updated is null)
        {
            return NotFound();
        }

        TempData["Success"] = "Семейная связь удалена.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            TempData["Error"] = "Файл пустой";
            return RedirectToAction(nameof(Index));
        }

        await using var stream = file.OpenReadStream();
        var result = await accountService.ImportAsync(stream, file.FileName, ActorId, ClientIp, cancellationToken);
        TempData["Success"] = $"Импорт завершен: total={result.Total}, created={result.Created}, updated={result.Updated}, errors={result.Errors.Count}";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] AccountFilterRequest request, CancellationToken cancellationToken)
    {
        var data = await accountService.ExportCsvAsync(request, cancellationToken);
        return File(data, "text/csv", $"accounts-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static void NormalizeTags(AccountUpsertRequest request)
    {
        if (request.Tags.Count != 1)
        {
            return;
        }

        var single = request.Tags[0];
        if (string.IsNullOrWhiteSpace(single))
        {
            request.Tags = [];
            return;
        }

        request.Tags = single
            .Split([',', '|', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
