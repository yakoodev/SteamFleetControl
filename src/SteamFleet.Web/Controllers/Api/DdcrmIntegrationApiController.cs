using Microsoft.AspNetCore.Mvc;

namespace SteamFleet.Web.Controllers.Api;

[ApiController]
[Route("internal/v1/ddcrm")]
[ApiExplorerSettings(IgnoreApi = true)]
[IgnoreAntiforgeryToken]
public sealed class DdcrmIntegrationApiController(
    ILogger<DdcrmIntegrationApiController> logger) : ControllerBase
{
    [HttpPost("project-tokens/upsert")]
    public Task<IActionResult> UpsertProjectTokenAsync([FromBody] ProjectTokenUpsertRequest request, CancellationToken cancellationToken)
    {
        logger.LogWarning("Legacy DDCRM endpoint called: project-tokens/upsert projectId={ProjectId}", request.ProjectId);
        return Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status410Gone, new
        {
            error = "legacy ddcrm service-token API is disabled",
            migration = "/internal/v2/worker/*",
        }));
    }

    [HttpPost("project-tokens/revoke")]
    public Task<IActionResult> RevokeProjectTokenAsync([FromBody] ProjectTokenRevokeRequest request, CancellationToken cancellationToken)
    {
        logger.LogWarning("Legacy DDCRM endpoint called: project-tokens/revoke projectId={ProjectId}", request.ProjectId);
        return Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status410Gone, new
        {
            error = "legacy ddcrm service-token API is disabled",
            migration = "/internal/v2/worker/*",
        }));
    }

    [HttpPost("integration/{scope}")]
    public Task<IActionResult> InvokeIntegrationAsync(string scope, [FromBody] IntegrationInvokeRequest request, CancellationToken cancellationToken)
    {
        logger.LogWarning("Legacy DDCRM endpoint called: integration/{Scope} projectId={ProjectId}", scope, request.ProjectId);
        return Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status410Gone, new
        {
            error = "legacy ddcrm service-token API is disabled",
            migration = "/internal/v2/worker/*",
        }));
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
