using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Steam;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;

namespace SteamFleet.Web.Controllers.Api;

[ApiController]
[IgnoreAntiforgeryToken]
[Authorize]
[Route("api/accounts")]
public sealed class AccountsApiController(IAccountService accountService) : ControllerBase
{
    private string ActorId => User.Identity?.Name ?? "system";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet]
    public Task<AccountsPageResult> GetAccounts([FromQuery] AccountFilterRequest request, CancellationToken cancellationToken)
        => accountService.GetAsync(request, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AccountDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var account = await accountService.GetByIdAsync(id, cancellationToken);
        return account is null ? NotFound() : Ok(account);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [HttpPost]
    public async Task<ActionResult<AccountDto>> Create([FromBody] AccountUpsertRequest request, CancellationToken cancellationToken)
    {
        var account = await accountService.CreateAsync(request, ActorId, ClientIp, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = account.Id }, account);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AccountDto>> Update(Guid id, [FromBody] AccountUpsertRequest request, CancellationToken cancellationToken)
    {
        var account = await accountService.UpdateAsync(id, request, ActorId, ClientIp, cancellationToken);
        return account is null ? NotFound() : Ok(account);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await accountService.ArchiveAsync(id, ActorId, ClientIp, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("import")]
    public async Task<ActionResult<AccountImportResult>> Import([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return BadRequest("File is empty");
        }

        await using var stream = file.OpenReadStream();
        var result = await accountService.ImportAsync(stream, file.FileName, ActorId, ClientIp, cancellationToken);
        return Ok(result);
    }

    [HttpGet("export")]
    public async Task<FileContentResult> Export([FromQuery] AccountFilterRequest request, CancellationToken cancellationToken)
    {
        var data = await accountService.ExportCsvAsync(request, cancellationToken);
        return File(data, "text/csv", $"accounts-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/authenticate")]
    public Task<SteamFleet.Contracts.Steam.SteamAuthResult> Authenticate(
        Guid id,
        [FromBody] AccountAuthenticateRequest request,
        CancellationToken cancellationToken)
        => accountService.AuthenticateAsync(id, request, ActorId, ClientIp, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("qr/start")]
    public async Task<ActionResult<AccountQrOnboardingStartResult>> StartQrOnboarding(CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.StartQrOnboardingAsync(ActorId, ClientIp, cancellationToken);
            return Ok(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpGet("qr/{flowId:guid}")]
    public async Task<ActionResult<AccountQrOnboardingPollResult>> PollQrOnboarding(Guid flowId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.PollQrOnboardingAsync(flowId, ActorId, ClientIp, cancellationToken);
            if (result.Status == AccountQrOnboardingStatus.Conflict)
            {
                return Conflict(result);
            }

            return Ok(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("qr/{flowId:guid}/cancel")]
    public async Task<IActionResult> CancelQrOnboarding(Guid flowId, CancellationToken cancellationToken)
    {
        try
        {
            await accountService.CancelQrOnboardingAsync(flowId, ActorId, ClientIp, cancellationToken);
            return NoContent();
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/authenticate/qr/start")]
    public Task<SteamFleet.Contracts.Steam.SteamQrAuthStartResult> StartQrAuth(
        Guid id,
        CancellationToken cancellationToken)
        => accountService.StartQrAuthenticationAsync(id, ActorId, ClientIp, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpGet("{id:guid}/authenticate/qr/{flowId:guid}")]
    public Task<SteamFleet.Contracts.Steam.SteamQrAuthPollResult> PollQrAuth(
        Guid id,
        Guid flowId,
        CancellationToken cancellationToken)
        => accountService.PollQrAuthenticationAsync(id, flowId, ActorId, ClientIp, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/authenticate/qr/{flowId:guid}/cancel")]
    public Task CancelQrAuth(
        Guid id,
        Guid flowId,
        CancellationToken cancellationToken)
        => accountService.CancelQrAuthenticationAsync(id, flowId, ActorId, ClientIp, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/validate-session")]
    public Task<SteamFleet.Contracts.Steam.SteamSessionValidationResult> ValidateSession(Guid id, CancellationToken cancellationToken)
        => accountService.ValidateSessionAsync(id, ActorId, ClientIp, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/refresh-session")]
    public Task<SteamFleet.Contracts.Steam.SteamSessionInfo> RefreshSession(Guid id, CancellationToken cancellationToken)
        => accountService.RefreshSessionAsync(id, ActorId, ClientIp, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/password/change")]
    public async Task<ActionResult<AccountPasswordChangeResult>> ChangePassword(
        Guid id,
        [FromBody] AccountPasswordChangeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.ChangePasswordAsync(id, request, ActorId, ClientIp, cancellationToken);
            if (result.RequiresConfirmation)
            {
                return StatusCode(StatusCodes.Status409Conflict, result);
            }

            if (!result.Success)
            {
                return BadRequest(result);
            }

            return Ok(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/sessions/deauthorize")]
    public Task<SteamFleet.Contracts.Steam.SteamOperationResult> DeauthorizeSessions(Guid id, CancellationToken cancellationToken)
        => accountService.DeauthorizeAllSessionsAsync(id, ActorId, ClientIp, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/games/refresh")]
    public async Task<ActionResult<AccountGamesPageResult>> RefreshGames(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.RefreshGamesAsync(id, ActorId, ClientIp, cancellationToken);
            return Ok(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            var reasonCode = ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
                ? SteamReasonCodes.AuthSessionMissing
                : SteamReasonCodes.EndpointRejected;
            var status = reasonCode == SteamReasonCodes.AuthSessionMissing
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return StatusCode(status, new
            {
                errorMessage = ex.Message,
                reasonCode,
                retryable = reasonCode == SteamReasonCodes.AuthSessionMissing
            });
        }
    }

    [HttpGet("{id:guid}/games")]
    public async Task<ActionResult<AccountGamesPageResult>> GetGames(
        Guid id,
        [FromQuery] string? scope,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parsedScope = scope?.ToLowerInvariant() switch
        {
            "owned" => AccountGamesScope.Owned,
            "family" => AccountGamesScope.Family,
            _ => AccountGamesScope.All
        };

        var result = await accountService.GetGamesAsync(id, parsedScope, q, page, pageSize, cancellationToken);
        return Ok(result);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/family/sync")]
    public async Task<ActionResult<AccountFamilySnapshotDto>> SyncFamily(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.SyncFamilyFromSteamAsync(id, ActorId, ClientIp, cancellationToken);
            return Ok(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [HttpGet("{id:guid}/family")]
    public async Task<ActionResult<AccountFamilySnapshotDto>> GetFamily(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.GetFamilySnapshotAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/family/invite")]
    public async Task<ActionResult<SteamOperationResult>> InviteToFamily(
        Guid id,
        [FromBody] FamilyInviteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.InviteToFamilyAsync(id, request, ActorId, ClientIp, cancellationToken);
            return MapOperationResult(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/family/accept-invite")]
    public async Task<ActionResult<SteamOperationResult>> AcceptFamilyInvite(
        Guid id,
        [FromBody] FamilyAcceptInviteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.AcceptFamilyInviteAsync(id, request, ActorId, ClientIp, cancellationToken);
            return MapOperationResult(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/family/assign-parent")]
    public async Task<ActionResult<AccountDto>> AssignParent(
        Guid id,
        [FromBody] FamilyAssignParentRequest request,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return StatusCode(StatusCodes.Status409Conflict, new
        {
            errorMessage = "Manual family assignment is disabled. Use /api/accounts/{id}/family/sync.",
            reasonCode = SteamReasonCodes.EndpointRejected,
            retryable = false
        });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/family/remove-parent")]
    public async Task<ActionResult<AccountDto>> RemoveParent(Guid id, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return StatusCode(StatusCodes.Status409Conflict, new
        {
            errorMessage = "Manual family assignment is disabled. Use /api/accounts/{id}/family/sync.",
            reasonCode = SteamReasonCodes.EndpointRejected,
            retryable = false
        });
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/friends/invite-link/sync")]
    public async Task<ActionResult<FriendInviteLinkDto>> SyncInviteLink(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await accountService.SyncFriendInviteLinkAsync(id, ActorId, ClientIp, cancellationToken);
            return Ok(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            var reasonCode = ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
                ? SteamReasonCodes.AuthSessionMissing
                : SteamReasonCodes.EndpointRejected;
            var status = reasonCode == SteamReasonCodes.AuthSessionMissing
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return StatusCode(status, new
            {
                errorMessage = ex.Message,
                reasonCode,
                retryable = reasonCode == SteamReasonCodes.AuthSessionMissing
            });
        }
    }

    [HttpGet("{id:guid}/friends/invite-link")]
    public async Task<ActionResult<FriendInviteLinkDto>> GetInviteLink(Guid id, CancellationToken cancellationToken)
    {
        var result = await accountService.GetFriendInviteLinkAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/friends/accept-invite")]
    public async Task<ActionResult<SteamFleet.Contracts.Steam.SteamOperationResult>> AcceptInvite(
        Guid id,
        [FromBody] AcceptInviteRequest request,
        CancellationToken cancellationToken)
    {
        var targetAccountId = request.TargetAccountId ?? id;
        if (targetAccountId != id)
        {
            return BadRequest("targetAccountId must match account id in route.");
        }

        if (request.SourceAccountId == id)
        {
            return BadRequest("sourceAccountId must differ from target account id.");
        }

        try
        {
            var inviteUrl = request.InviteUrl?.Trim();
            if (string.IsNullOrWhiteSpace(inviteUrl))
            {
                if (request.SourceAccountId is null)
                {
                    return BadRequest(new
                    {
                        errorMessage = "Provide inviteUrl or sourceAccountId.",
                        reasonCode = SteamReasonCodes.InvalidInviteLink,
                        retryable = false
                    });
                }

                var synced = await accountService.SyncFriendInviteLinkAsync(request.SourceAccountId.Value, ActorId, ClientIp, cancellationToken);
                inviteUrl = synced.InviteUrl;
            }

            var result = await accountService.AcceptFriendInviteAsync(id, inviteUrl!, ActorId, ClientIp, cancellationToken);
            return Ok(result);
        }
        catch (SteamGatewayOperationException ex)
        {
            return MapGatewayError(ex);
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperationError(ex);
        }
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/friends/refresh")]
    public Task<AccountFriendsSnapshotDto> RefreshFriends(Guid id, CancellationToken cancellationToken)
        => accountService.RefreshFriendsAsync(id, ActorId, ClientIp, cancellationToken);

    [HttpGet("{id:guid}/friends")]
    public Task<AccountFriendsSnapshotDto> GetFriends(Guid id, CancellationToken cancellationToken)
        => accountService.GetFriendsAsync(id, cancellationToken);

    private ActionResult MapGatewayError(SteamGatewayOperationException ex)
    {
        var reasonCode = string.IsNullOrWhiteSpace(ex.ReasonCode)
            ? SteamReasonCodes.Unknown
            : ex.ReasonCode;
        var status = reasonCode is SteamReasonCodes.AuthSessionMissing or SteamReasonCodes.GuardPending
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;

        return StatusCode(status, new
        {
            errorMessage = ex.Message,
            reasonCode,
            retryable = ex.Retryable
        });
    }

    private ActionResult MapInvalidOperationError(InvalidOperationException ex)
    {
        var reasonCode = ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            ? SteamReasonCodes.AuthSessionMissing
            : SteamReasonCodes.EndpointRejected;

        var status = reasonCode == SteamReasonCodes.AuthSessionMissing
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;

        return StatusCode(status, new
        {
            errorMessage = ex.Message,
            reasonCode,
            retryable = reasonCode == SteamReasonCodes.AuthSessionMissing
        });
    }

    private ActionResult<SteamOperationResult> MapOperationResult(SteamOperationResult result)
    {
        if (result.Success)
        {
            return Ok(result);
        }

        var reasonCode = string.IsNullOrWhiteSpace(result.ReasonCode)
            ? SteamReasonCodes.Unknown
            : result.ReasonCode;
        var statusCode = reasonCode is SteamReasonCodes.AuthSessionMissing or SteamReasonCodes.GuardPending
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;

        return StatusCode(statusCode, result);
    }
}
