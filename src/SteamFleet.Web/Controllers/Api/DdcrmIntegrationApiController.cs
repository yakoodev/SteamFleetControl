using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence;

namespace SteamFleet.Web.Controllers.Api;

[ApiController]
[Route("internal/v1/ddcrm")]
public sealed class DdcrmIntegrationApiController(
    SteamFleetDbContext dbContext,
    IConfiguration configuration,
    ILogger<DdcrmIntegrationApiController> logger) : ControllerBase
{
    [HttpPost("project-tokens/upsert")]
    public async Task<IActionResult> UpsertProjectTokenAsync([FromBody] ProjectTokenUpsertRequest request, CancellationToken cancellationToken)
    {
        if (!TryRequireServiceToken(out var unauthorizedResult))
        {
            return unauthorizedResult!;
        }

        var scopes = NormalizeScopes(request.Scopes);
        if (scopes.Count == 0)
        {
            return BadRequest(new { error = "scopes must include read/jobs" });
        }

        var entity = await dbContext.DdcrmProjectTokens.SingleOrDefaultAsync(x => x.ProjectId == request.ProjectId, cancellationToken);
        if (entity is null)
        {
            entity = new DdcrmProjectToken
            {
                Id = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                TokenHashSha256 = ComputeTokenHash(request.Token),
                ScopesCsv = string.Join(',', scopes),
                Status = "active",
            };
            dbContext.DdcrmProjectTokens.Add(entity);
        }
        else
        {
            entity.TokenHashSha256 = ComputeTokenHash(request.Token);
            entity.ScopesCsv = string.Join(',', scopes);
            entity.Status = "active";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("DDCRM token upsert projectId={ProjectId} scopes={Scopes}", request.ProjectId, string.Join(',', scopes));
        return Ok(new { status = "completed", projectId = request.ProjectId });
    }

    [HttpPost("project-tokens/revoke")]
    public async Task<IActionResult> RevokeProjectTokenAsync([FromBody] ProjectTokenRevokeRequest request, CancellationToken cancellationToken)
    {
        if (!TryRequireServiceToken(out var unauthorizedResult))
        {
            return unauthorizedResult!;
        }

        var entity = await dbContext.DdcrmProjectTokens.SingleOrDefaultAsync(x => x.ProjectId == request.ProjectId, cancellationToken);
        if (entity is not null)
        {
            entity.Status = "revoked";
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("DDCRM token revoke projectId={ProjectId}", request.ProjectId);
        return Ok(new { status = "completed", projectId = request.ProjectId });
    }

    [HttpPost("integration/{scope}")]
    public async Task<IActionResult> InvokeIntegrationAsync(string scope, [FromBody] IntegrationInvokeRequest request, CancellationToken cancellationToken)
    {
        if (!TryRequireServiceToken(out var unauthorizedResult))
        {
            return unauthorizedResult!;
        }

        var normalizedScope = scope.Trim().ToLowerInvariant();
        if (normalizedScope is not ("read" or "jobs"))
        {
            return BadRequest(new { error = "scope must be read/jobs" });
        }

        var projectToken = Request.Headers["X-Project-Service-Token"].ToString();
        if (string.IsNullOrWhiteSpace(projectToken))
        {
            return Unauthorized(new { error = "X-Project-Service-Token is required" });
        }

        var entity = await dbContext.DdcrmProjectTokens.SingleOrDefaultAsync(
            x => x.ProjectId == request.ProjectId,
            cancellationToken);

        if (entity is null || !string.Equals(entity.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("DDCRM denied by grant projectId={ProjectId} scope={Scope}", request.ProjectId, normalizedScope);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "project token is not active" });
        }

        if (!string.Equals(entity.TokenHashSha256, ComputeTokenHash(projectToken), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("DDCRM denied by token mismatch projectId={ProjectId} scope={Scope}", request.ProjectId, normalizedScope);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "project token mismatch" });
        }

        var allowedScopes = SplitScopes(entity.ScopesCsv);
        if (!allowedScopes.Contains(normalizedScope))
        {
            logger.LogWarning("DDCRM denied by scope projectId={ProjectId} scope={Scope} allowed={Allowed}", request.ProjectId, normalizedScope, string.Join(',', allowedScopes));
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "scope denied" });
        }

        if (normalizedScope == "read")
        {
            var accountsCount = await dbContext.SteamAccounts.CountAsync(cancellationToken);
            var jobsCount = await dbContext.Jobs.CountAsync(cancellationToken);
            return Ok(new
            {
                result = new
                {
                    service = "steam-accounts-manager",
                    projectId = request.ProjectId,
                    scope = normalizedScope,
                    accountsCount,
                    jobsCount,
                },
            });
        }

        return Ok(new
        {
            result = new
            {
                service = "steam-accounts-manager",
                projectId = request.ProjectId,
                scope = normalizedScope,
                accepted = true,
                payload = request.Payload ?? new Dictionary<string, object?>(),
            },
        });
    }

    private bool TryRequireServiceToken(out IActionResult? unauthorizedResult)
    {
        unauthorizedResult = null;

        var enabled = configuration.GetValue("DDCRM_SERVICE_AUTH_ENABLED", true);
        if (!enabled)
        {
            return true;
        }

        var acceptedTokens = configuration["DDCRM_SERVICE_AUTH_TOKENS"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal) ?? [];

        if (acceptedTokens.Count == 0)
        {
            unauthorizedResult = StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "DDCRM_SERVICE_AUTH_TOKENS is empty" });
            return false;
        }

        var serviceToken = Request.Headers["X-Service-Token"].ToString();
        if (string.IsNullOrWhiteSpace(serviceToken) || !acceptedTokens.Contains(serviceToken))
        {
            unauthorizedResult = Unauthorized(new { error = "invalid service token" });
            return false;
        }

        return true;
    }

    private string ComputeTokenHash(string token)
    {
        var salt = configuration["DDCRM_PROJECT_TOKEN_SIGNING_SALT"] ?? "steamfleet-default-project-token-salt";
        var payload = $"{salt}:{token}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static List<string> NormalizeScopes(IReadOnlyCollection<string>? scopes)
    {
        return (scopes ?? [])
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x is "read" or "jobs")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<string> SplitScopes(string scopesCsv)
    {
        return scopesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record ProjectTokenUpsertRequest(
    Guid ProjectId,
    string Token,
    IReadOnlyCollection<string>? Scopes);

public sealed record ProjectTokenRevokeRequest(Guid ProjectId);

public sealed record IntegrationInvokeRequest(
    Guid ProjectId,
    Dictionary<string, object?>? Payload);
