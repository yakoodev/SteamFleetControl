using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Contracts.Steam;
using SteamFleet.Integrations.Steam;
using SteamFleet.Integrations.Steam.Options;
using SteamFleet.Persistence;
using SteamFleet.Persistence.Security;
using SteamFleet.Persistence.Services;

namespace SteamFleet.IntegrationTests;

public sealed class AccountsAndJobsFlowTests
{
    private static readonly SteamKitGateway Gateway = new(
        NullLogger<SteamKitGateway>.Instance,
        Options.Create(new SteamGatewayOptions()));

    [Fact]
    public async Task CreateAccount_And_ValidateSession_And_RunJob_WhenLiveCredsProvided()
    {
        var login = Environment.GetEnvironmentVariable("STEAM_TEST_LOGIN");
        var password = Environment.GetEnvironmentVariable("STEAM_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<SteamFleetDbContext>()
            .UseInMemoryDatabase($"steamfleet-tests-{Guid.NewGuid()}")
            .Options;

        await using var dbContext = new SteamFleetDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var crypto = new AesGcmSecretCryptoService(Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()));
        var audit = new AuditService(dbContext);
        var accountService = new AccountService(dbContext, Gateway, crypto, audit);
        var jobService = new JobService(dbContext, Gateway, crypto, audit);

        var auth = await Gateway.AuthenticateAsync(new SteamCredentials
        {
            LoginName = login,
            Password = password,
            SharedSecret = Environment.GetEnvironmentVariable("STEAM_TEST_SHARED_SECRET"),
            GuardCode = Environment.GetEnvironmentVariable("STEAM_TEST_GUARD_CODE"),
            AllowDeviceConfirmation = true
        });

        Assert.True(auth.Success);
        Assert.False(string.IsNullOrWhiteSpace(auth.Session.CookiePayload));

        var account = await accountService.CreateAsync(new AccountUpsertRequest
        {
            LoginName = $"{login}-itest-{Guid.NewGuid():N}",
            DisplayName = "Account One",
            Password = password,
            SessionPayload = auth.Session.CookiePayload,
            Tags = ["seed"]
        }, "tester", "127.0.0.1");

        var validation = await accountService.ValidateSessionAsync(account.Id, "tester", "127.0.0.1");
        Assert.True(validation.IsValid);

        var job = await jobService.CreateAsync(new JobCreateRequest
        {
            Type = JobType.SessionValidate,
            AccountIds = [account.Id],
            DryRun = false,
            Parallelism = 2,
            RetryCount = 1
        }, "tester", "127.0.0.1");

        await jobService.ExecuteAsync(job.Id);

        var jobState = await jobService.GetByIdAsync(job.Id);
        Assert.NotNull(jobState);
        Assert.Equal(1, jobState!.TotalCount);
        Assert.True(jobState.Status is JobStatus.Completed or JobStatus.Failed);
        Assert.Equal(1, jobState.SuccessCount + jobState.FailureCount);

        var auditEvents = await audit.GetAsync(0, 200);
        Assert.NotEmpty(auditEvents);
    }
}
