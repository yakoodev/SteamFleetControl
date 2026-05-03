using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Persistence;
using SteamFleet.Persistence.Services;
using SteamFleet.Web.Controllers.Api;

namespace SteamFleet.IntegrationTests;

public sealed class WorkerIntegrationAdapterControllerTests
{
    [Fact]
    public void Health_WithoutServiceToken_ReturnsUnauthorized()
    {
        using var dbContext = CreateDbContext();
        var controller = CreateController(dbContext);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var response = controller.Health();
        Assert.IsType<UnauthorizedObjectResult>(response);
    }

    [Fact]
    public async Task ReadAndJobsActions_WithValidToken_ReturnExpectedPayload()
    {
        using var dbContext = CreateDbContext();
        var accountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var accountService = new Mock<IAccountService>();
        accountService
            .Setup(x => x.GetAsync(It.IsAny<AccountFilterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountsPageResult
            {
                Items =
                [
                    new AccountDto
                    {
                        Id = accountId,
                        LoginName = "steam-user",
                        DisplayName = "Steam User",
                        Status = AccountStatus.Active,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ddcrm.runtimeAccountId"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            ["ddcrm.projectId"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            ["ddcrm.storageNamespace"] = "steam-bbbbbbbb-bbbbbbbb",
                        },
                    },
                ],
                TotalCount = 1,
            });
        accountService
            .Setup(x => x.CreateAsync(
                It.IsAny<AccountUpsertRequest>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountUpsertRequest request, string _, string? _, CancellationToken _) => new AccountDto
            {
                Id = Guid.NewGuid(),
                LoginName = request.LoginName,
                DisplayName = request.DisplayName,
                Email = request.Email,
                Proxy = request.Proxy,
                Status = request.Status,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Metadata = request.Metadata,
                Tags = request.Tags,
            });

        var controller = CreateController(dbContext, accountService: accountService.Object);

        controller.ControllerContext = CreateHttpContext("svc-token-a");
        var read = await controller.InvokeActionAsync(
            "ext.integration.steam.read",
            new WorkerActionRequest(JsonSerializer.SerializeToElement(new
            {
                operation = "accounts.list",
                page = 1,
                pageSize = 50,
            })),
            CancellationToken.None);
        var readResult = Assert.IsType<OkObjectResult>(read);
        Assert.Equal(StatusCodes.Status200OK, readResult.StatusCode ?? StatusCodes.Status200OK);

        controller.ControllerContext = CreateHttpContext("svc-token-a");
        var jobs = await controller.InvokeActionAsync(
            "ext.integration.steam.jobs",
            new WorkerActionRequest(JsonSerializer.SerializeToElement(new
            {
                operation = "accounts.create",
                account = new
                {
                    loginName = "new-steam-account",
                    displayName = "New Steam",
                    status = "Active",
                },
            })),
            CancellationToken.None);
        var jobsResult = Assert.IsType<OkObjectResult>(jobs);
        Assert.Equal(StatusCodes.Status200OK, jobsResult.StatusCode ?? StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ProxyApplyAction_ReturnsNoOpAccepted()
    {
        using var dbContext = CreateDbContext();
        var controller = CreateController(dbContext);
        controller.ControllerContext = CreateHttpContext("svc-token-a");

        var response = await controller.InvokeActionAsync(
            "ext.account.proxy-credentials.apply",
            new WorkerActionRequest(JsonSerializer.SerializeToElement(new { host = "127.0.0.1" })),
            CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode ?? StatusCodes.Status200OK);
    }

    [Fact]
    public async Task UnsupportedAction_ReturnsBadRequest()
    {
        using var dbContext = CreateDbContext();
        var controller = CreateController(dbContext);
        controller.ControllerContext = CreateHttpContext("svc-token-a");

        var response = await controller.InvokeActionAsync(
            "ext.integration.steam.write",
            new WorkerActionRequest(JsonSerializer.SerializeToElement(new { operation = "noop" })),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response);
    }

    private static SteamFleetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SteamFleetDbContext>()
            .UseInMemoryDatabase($"steamfleet-worker-adapter-{Guid.NewGuid():N}")
            .Options;
        return new SteamFleetDbContext(options);
    }

    private static WorkerIntegrationAdapterController CreateController(
        SteamFleetDbContext dbContext,
        IAccountService? accountService = null,
        IJobService? jobService = null,
        IBackgroundJobClient? backgroundJobs = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WORKER_API_SERVICE_AUTH_ENABLED"] = "true",
                ["WORKER_API_SERVICE_AUTH_ACCEPTED_TOKENS"] = "svc-token-a",
                ["DDCRM_WORKER_ACCOUNT_ID"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["DDCRM_WORKER_PROJECT_ID"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                ["STEAM_ACCOUNTS_STORAGE_NAMESPACE"] = "steam-bbbbbbbb-bbbbbbbb",
            })
            .Build();

        var defaultAccountService = accountService ?? Mock.Of<IAccountService>();
        var defaultJobService = jobService ?? Mock.Of<IJobService>();
        var defaultBackgroundJobs = backgroundJobs ?? Mock.Of<IBackgroundJobClient>();

        return new WorkerIntegrationAdapterController(
            dbContext,
            configuration,
            defaultAccountService,
            defaultJobService,
            defaultBackgroundJobs,
            NullLogger<WorkerIntegrationAdapterController>.Instance);
    }

    private static ControllerContext CreateHttpContext(string serviceToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Service-Token"] = serviceToken;
        return new ControllerContext { HttpContext = context };
    }
}
