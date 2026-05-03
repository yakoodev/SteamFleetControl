using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SteamFleet.Web.Controllers.Api;

namespace SteamFleet.IntegrationTests;

public sealed class DdcrmIntegrationApiControllerTests
{
    [Fact]
    public async Task LegacyUpsertEndpoint_ReturnsGone()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        var response = await controller.UpsertProjectTokenAsync(
            new ProjectTokenUpsertRequest(Guid.NewGuid(), "token-123456789", ["read"]),
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status410Gone, result.StatusCode);
    }

    [Fact]
    public async Task LegacyInvokeEndpoint_ReturnsGone()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        var response = await controller.InvokeIntegrationAsync(
            "read",
            new IntegrationInvokeRequest(Guid.NewGuid(), new Dictionary<string, object?>()),
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status410Gone, result.StatusCode);
    }

    private static DdcrmIntegrationApiController CreateController()
    {
        return new DdcrmIntegrationApiController(
            NullLogger<DdcrmIntegrationApiController>.Instance);
    }
}
