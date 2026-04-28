using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SteamFleet.Persistence;
using SteamFleet.Web.Controllers.Api;

namespace SteamFleet.IntegrationTests;

public sealed class DdcrmIntegrationApiControllerTests
{
    [Fact]
    public async Task UpsertProjectToken_WithoutServiceToken_ReturnsUnauthorized()
    {
        await using var dbContext = CreateDbContext();
        var controller = CreateController(dbContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        var response = await controller.UpsertProjectTokenAsync(
            new ProjectTokenUpsertRequest(Guid.NewGuid(), "token-123456789", ["read"]),
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(response);
    }

    [Fact]
    public async Task ProjectTokenLifecycle_EnforcesScopesAndRevocation()
    {
        await using var dbContext = CreateDbContext();
        var controller = CreateController(dbContext);
        var projectId = Guid.NewGuid();
        const string projectToken = "project-token-123456789";

        controller.ControllerContext = CreateHttpContext("svc-token-a", null);
        var upsert = await controller.UpsertProjectTokenAsync(
            new ProjectTokenUpsertRequest(projectId, projectToken, ["read"]),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(upsert);

        controller.ControllerContext = CreateHttpContext("svc-token-a", projectToken);
        var readInvoke = await controller.InvokeIntegrationAsync(
            "read",
            new IntegrationInvokeRequest(projectId, new Dictionary<string, object?>()),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(readInvoke);

        controller.ControllerContext = CreateHttpContext("svc-token-a", projectToken);
        var deniedJobs = await controller.InvokeIntegrationAsync(
            "jobs",
            new IntegrationInvokeRequest(projectId, new Dictionary<string, object?>()),
            CancellationToken.None);
        var deniedJobsResult = Assert.IsType<ObjectResult>(deniedJobs);
        Assert.Equal(StatusCodes.Status403Forbidden, deniedJobsResult.StatusCode);

        controller.ControllerContext = CreateHttpContext("svc-token-a", null);
        var revoke = await controller.RevokeProjectTokenAsync(
            new ProjectTokenRevokeRequest(projectId),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(revoke);

        controller.ControllerContext = CreateHttpContext("svc-token-a", projectToken);
        var deniedAfterRevoke = await controller.InvokeIntegrationAsync(
            "read",
            new IntegrationInvokeRequest(projectId, new Dictionary<string, object?>()),
            CancellationToken.None);
        var deniedAfterRevokeResult = Assert.IsType<ObjectResult>(deniedAfterRevoke);
        Assert.Equal(StatusCodes.Status403Forbidden, deniedAfterRevokeResult.StatusCode);
    }

    private static SteamFleetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SteamFleetDbContext>()
            .UseInMemoryDatabase($"steamfleet-ddcrm-tests-{Guid.NewGuid():N}")
            .Options;
        return new SteamFleetDbContext(options);
    }

    private static DdcrmIntegrationApiController CreateController(SteamFleetDbContext dbContext)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DDCRM_SERVICE_AUTH_ENABLED"] = "true",
                ["DDCRM_SERVICE_AUTH_TOKENS"] = "svc-token-a",
                ["DDCRM_PROJECT_TOKEN_SIGNING_SALT"] = "test-salt",
            })
            .Build();

        return new DdcrmIntegrationApiController(
            dbContext,
            configuration,
            NullLogger<DdcrmIntegrationApiController>.Instance);
    }

    private static ControllerContext CreateHttpContext(string serviceToken, string? projectToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Service-Token"] = serviceToken;
        if (!string.IsNullOrWhiteSpace(projectToken))
        {
            context.Request.Headers["X-Project-Service-Token"] = projectToken;
        }

        return new ControllerContext { HttpContext = context };
    }
}
