using System.Text;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence;
using SteamFleet.Persistence.Security;
using SteamFleet.Persistence.Services;

namespace SteamFleet.IntegrationTests;

public sealed class NewFeaturesFlowTests
{
    [Fact]
    public async Task Import100_ThenPasswordAndDeauthorizeJobs_Work_WithOneTimeReport()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var gateway = new FakeSteamGateway();
        var accountService = new AccountService(dbContext, gateway, crypto, auditService);
        var jobService = new JobService(dbContext, gateway, crypto, auditService);

        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("login,displayName,password,email,tags,folder");
        for (var i = 1; i <= 100; i++)
        {
            csvBuilder.AppendLine($"login{i:D3},Display {i},StartPass1!,user{i:D3}@mail.local,tagA|tagB,seed-folder");
        }

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvBuilder.ToString()));
        var importResult = await accountService.ImportAsync(stream, "accounts.csv", "itest", "127.0.0.1");
        Assert.Equal(100, importResult.Created);
        Assert.Empty(importResult.Errors);

        var page = await accountService.GetAsync(new AccountFilterRequest { Page = 1, PageSize = 200 });
        Assert.Equal(100, page.TotalCount);

        var selected = page.Items.Take(20).Select(x => x.Id).ToList();
        var passwordJob = await jobService.CreateAsync(new JobCreateRequest
        {
            Type = JobType.PasswordChange,
            AccountIds = selected,
            DryRun = false,
            Parallelism = 5,
            RetryCount = 1,
            Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generateLength"] = "24",
                ["deauthorizeAfterChange"] = "false"
            }
        }, "itest", "127.0.0.1");

        await jobService.ExecuteAsync(passwordJob.Id);

        var passwordJobState = await jobService.GetByIdAsync(passwordJob.Id);
        Assert.NotNull(passwordJobState);
        Assert.True(passwordJobState!.HasSensitiveReport);
        Assert.Equal(20, passwordJobState.SuccessCount);

        var report = await jobService.DownloadSensitiveReportOnceAsync(passwordJob.Id, "itest", "127.0.0.1");
        Assert.NotNull(report);
        var lines = Encoding.UTF8.GetString(report!).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(21, lines.Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jobService.DownloadSensitiveReportOnceAsync(passwordJob.Id, "itest", "127.0.0.1"));

        var deauthJob = await jobService.CreateAsync(new JobCreateRequest
        {
            Type = JobType.SessionsDeauthorize,
            AccountIds = selected,
            DryRun = false,
            Parallelism = 5,
            RetryCount = 1
        }, "itest", "127.0.0.1");

        await jobService.ExecuteAsync(deauthJob.Id);

        var deauthState = await jobService.GetByIdAsync(deauthJob.Id);
        Assert.NotNull(deauthState);
        Assert.Equal(20, deauthState!.SuccessCount);
        Assert.Equal(0, deauthState.FailureCount);

        var refreshedAccounts = await accountService.GetAsync(new AccountFilterRequest { Page = 1, PageSize = 200 });
        var statusMap = refreshedAccounts.Items
            .Where(x => selected.Contains(x.Id))
            .ToDictionary(x => x.Id, x => x.Status);
        Assert.Equal(20, statusMap.Count);
        Assert.All(statusMap.Values, status => Assert.Equal(AccountStatus.RequiresRelogin, status));

        var events = await auditService.GetAsync(0, 5000);
        Assert.Equal(20, events.Count(x => x.EventType == AuditEventType.PasswordChanged));
        Assert.Equal(20, events.Count(x => x.EventType == AuditEventType.SessionsDeauthorized));
        Assert.Contains(events, x => x.EventType == AuditEventType.SensitiveReportDownloaded);
    }

    [Fact]
    public async Task FamilyAndGamesFlow_AggregatesOwnedAndFamilyGames()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var gateway = new FakeSteamGateway();
        var accountService = new AccountService(dbContext, gateway, crypto, auditService);

        var parent = CreateAccount("main", crypto);
        var child = CreateAccount("child", crypto);
        await dbContext.SteamAccounts.AddRangeAsync(parent, child);
        await dbContext.SaveChangesAsync();

        await accountService.AssignParentAsync(child.Id, parent.Id, "itest", "127.0.0.1");

        var now = DateTimeOffset.UtcNow;
        parent.ProfileUrl = "https://steamcommunity.com/profiles/76561198000000001";
        parent.GamesCount = 2;
        parent.GamesLastSyncAt = now;
        child.ProfileUrl = "https://steamcommunity.com/profiles/76561198000000002";
        child.GamesCount = 2;
        child.GamesLastSyncAt = now;

        await dbContext.SteamAccountGames.AddRangeAsync(
            new SteamAccountGame
            {
                AccountId = parent.Id,
                AppId = 100,
                Name = "Half-Life",
                PlaytimeMinutes = 120,
                LastSyncedAt = now
            },
            new SteamAccountGame
            {
                AccountId = parent.Id,
                AppId = 200,
                Name = "Portal",
                PlaytimeMinutes = 30,
                LastSyncedAt = now
            },
            new SteamAccountGame
            {
                AccountId = child.Id,
                AppId = 200,
                Name = "Portal",
                PlaytimeMinutes = 300,
                LastSyncedAt = now
            },
            new SteamAccountGame
            {
                AccountId = child.Id,
                AppId = 300,
                Name = "Left 4 Dead",
                PlaytimeMinutes = 45,
                LastSyncedAt = now
            });
        await dbContext.SaveChangesAsync();

        var childCard = await accountService.GetByIdAsync(child.Id);
        Assert.NotNull(childCard);
        Assert.False(string.IsNullOrWhiteSpace(childCard!.ProfileUrl));
        Assert.Equal(2, childCard.GamesCount);

        var all = await accountService.GetGamesAsync(child.Id, AccountGamesScope.All, null, 1, 50);
        Assert.Equal(3, all.TotalCount);

        var ownPortal = Assert.Single(all.Items, x => x.AppId == 200);
        Assert.Equal(AccountGameAvailability.Owned, ownPortal.Availability);
        Assert.Equal(child.Id, ownPortal.SourceAccountId);

        var familyHalfLife = Assert.Single(all.Items, x => x.AppId == 100);
        Assert.Equal(AccountGameAvailability.FamilyGroup, familyHalfLife.Availability);
        Assert.Equal(parent.Id, familyHalfLife.SourceAccountId);
    }

    [Fact]
    public async Task QrOnboarding_Completed_CreatesAccount()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var gateway = new FakeSteamGateway
        {
            NextQrPollResult = new SteamQrAuthPollResult
            {
                Status = SteamQrAuthStatus.Completed,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                AuthResult = new SteamAuthResult
                {
                    Success = true,
                    AccountName = "qr-onboard-user",
                    SteamId64 = "76561198012345678",
                    Session = new SteamSessionInfo
                    {
                        CookiePayload = "session::qr-onboard-user",
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
                    }
                }
            }
        };
        var accountService = new AccountService(dbContext, gateway, crypto, auditService);

        var start = await accountService.StartQrOnboardingAsync("itest", "127.0.0.1");
        Assert.NotEqual(Guid.Empty, start.FlowId);
        Assert.StartsWith("data:image/png;base64,", start.QrImageDataUrl);
        Assert.False(string.IsNullOrWhiteSpace(start.ChallengeUrl));

        var poll = await accountService.PollQrOnboardingAsync(start.FlowId, "itest", "127.0.0.1");
        Assert.Equal(AccountQrOnboardingStatus.Completed, poll.Status);
        Assert.Equal(SteamReasonCodes.None, poll.ReasonCode);
        Assert.NotNull(poll.CreatedAccount);
        Assert.Equal("qr-onboard-user", poll.CreatedAccount!.LoginName);
        Assert.Null(poll.ExistingAccount);

        var created = await dbContext.SteamAccounts.Include(x => x.Secret).SingleAsync();
        Assert.Equal("qr-onboard-user", created.LoginName);
        Assert.Equal("76561198012345678", created.SteamId64);
        Assert.NotNull(created.Secret);
        Assert.False(string.IsNullOrWhiteSpace(created.Secret!.EncryptedSessionPayload));

        var events = await auditService.GetAsync(0, 200);
        Assert.Contains(events, x => x.EventType == AuditEventType.AccountCreated && x.EntityId == created.Id.ToString());
    }

    [Fact]
    public async Task QrOnboarding_DuplicateLogin_ReturnsConflict_WithoutCreate()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var gateway = new FakeSteamGateway
        {
            NextQrPollResult = new SteamQrAuthPollResult
            {
                Status = SteamQrAuthStatus.Completed,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                AuthResult = new SteamAuthResult
                {
                    Success = true,
                    AccountName = "existing-user",
                    SteamId64 = "76561198077777777",
                    Session = new SteamSessionInfo
                    {
                        CookiePayload = "session::existing-user",
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
                    }
                }
            }
        };
        var accountService = new AccountService(dbContext, gateway, crypto, auditService);

        await accountService.CreateAsync(new AccountUpsertRequest
        {
            LoginName = "existing-user",
            DisplayName = "Existing",
            Password = "Pass1234!"
        }, "itest", "127.0.0.1");

        var start = await accountService.StartQrOnboardingAsync("itest", "127.0.0.1");
        var poll = await accountService.PollQrOnboardingAsync(start.FlowId, "itest", "127.0.0.1");

        Assert.Equal(AccountQrOnboardingStatus.Conflict, poll.Status);
        Assert.Equal(SteamReasonCodes.DuplicateAccount, poll.ReasonCode);
        Assert.NotNull(poll.ExistingAccount);
        Assert.Equal("existing-user", poll.ExistingAccount!.LoginName);
        Assert.Null(poll.CreatedAccount);
        Assert.Equal(1, await dbContext.SteamAccounts.CountAsync());
    }

    [Fact]
    public async Task QrOnboarding_DuplicateSteamId_ReturnsConflict_WithoutCreate()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var gateway = new FakeSteamGateway
        {
            NextQrPollResult = new SteamQrAuthPollResult
            {
                Status = SteamQrAuthStatus.Completed,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                AuthResult = new SteamAuthResult
                {
                    Success = true,
                    AccountName = "new-login-name",
                    SteamId64 = "76561198055555555",
                    Session = new SteamSessionInfo
                    {
                        CookiePayload = "session::new-login-name",
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
                    }
                }
            }
        };
        var accountService = new AccountService(dbContext, gateway, crypto, auditService);

        await accountService.CreateAsync(new AccountUpsertRequest
        {
            LoginName = "old-login",
            DisplayName = "Existing",
            Password = "Pass1234!"
        }, "itest", "127.0.0.1");

        var existing = await dbContext.SteamAccounts.SingleAsync();
        existing.SteamId64 = "76561198055555555";
        await dbContext.SaveChangesAsync();

        var start = await accountService.StartQrOnboardingAsync("itest", "127.0.0.1");
        var poll = await accountService.PollQrOnboardingAsync(start.FlowId, "itest", "127.0.0.1");

        Assert.Equal(AccountQrOnboardingStatus.Conflict, poll.Status);
        Assert.Equal(SteamReasonCodes.DuplicateAccount, poll.ReasonCode);
        Assert.NotNull(poll.ExistingAccount);
        Assert.Equal(existing.Id, poll.ExistingAccount!.Id);
        Assert.Equal(1, await dbContext.SteamAccounts.CountAsync());
    }

    private static SteamFleetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SteamFleetDbContext>()
            .UseInMemoryDatabase($"steamfleet-itest-{Guid.NewGuid():N}")
            .Options;
        return new SteamFleetDbContext(options);
    }

    private static AesGcmSecretCryptoService CreateCrypto()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        return new AesGcmSecretCryptoService(Convert.ToBase64String(key));
    }

    private static SteamAccount CreateAccount(string login, ISecretCryptoService crypto)
    {
        var account = new SteamAccount
        {
            LoginName = login,
            DisplayName = login,
            Status = AccountStatus.Active
        };

        account.Secret = new SteamAccountSecret
        {
            AccountId = account.Id,
            EncryptedPassword = crypto.Encrypt("StartPass1!"),
            EncryptedSessionPayload = crypto.Encrypt($"session::{login}"),
            EncryptionVersion = crypto.Version
        };

        return account;
    }

    private sealed class FakeSteamGateway : ISteamAccountGateway
    {
        public Dictionary<string, SteamOwnedGamesSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);
        public SteamQrAuthPollResult NextQrPollResult { get; set; } = new()
        {
            Status = SteamQrAuthStatus.Completed,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            AuthResult = new SteamAuthResult
            {
                Success = true,
                AccountName = "qr-user",
                SteamId64 = "76561198000000000",
                Session = new SteamSessionInfo { CookiePayload = "session::qr" }
            }
        };

        public Task<SteamAuthResult> AuthenticateAsync(SteamCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamAuthResult
            {
                Success = true,
                SteamId64 = "76561198000000000",
                AccountName = credentials.LoginName,
                Session = new SteamSessionInfo
                {
                    CookiePayload = $"session::{credentials.LoginName}",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
                }
            });

        public Task<SteamQrAuthStartResult> StartQrAuthenticationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamQrAuthStartResult
            {
                FlowId = Guid.NewGuid(),
                ChallengeUrl = "https://steam.test/qr",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                PollingIntervalSeconds = 2
            });

        public Task<SteamQrAuthPollResult> PollQrAuthenticationAsync(Guid flowId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamQrAuthPollResult
            {
                FlowId = flowId,
                Status = NextQrPollResult.Status,
                ChallengeUrl = NextQrPollResult.ChallengeUrl,
                ExpiresAt = NextQrPollResult.ExpiresAt,
                ErrorMessage = NextQrPollResult.ErrorMessage,
                AuthResult = NextQrPollResult.AuthResult
            });

        public Task CancelQrAuthenticationAsync(Guid flowId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SteamSessionValidationResult> ValidateSessionAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamSessionValidationResult
            {
                IsValid = !string.IsNullOrWhiteSpace(sessionPayload),
                Reason = string.IsNullOrWhiteSpace(sessionPayload) ? "empty" : null
            });

        public Task<SteamSessionInfo> RefreshSessionAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamSessionInfo
            {
                CookiePayload = $"{sessionPayload}::refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
            });

        public Task<SteamProfileData> GetProfileAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamProfileData());

        public Task<SteamOperationResult> UpdateProfileAsync(string sessionPayload, SteamProfileData profileData, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamOperationResult { Success = true });

        public Task<SteamOperationResult> UpdateAvatarAsync(string sessionPayload, byte[] avatarBytes, string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamOperationResult { Success = true });

        public Task<SteamPrivacySettings> GetPrivacySettingsAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamPrivacySettings());

        public Task<SteamOperationResult> UpdatePrivacySettingsAsync(string sessionPayload, SteamPrivacySettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamOperationResult { Success = true });

        public Task<SteamOperationResult> ChangePasswordAsync(
            string sessionPayload,
            string currentPassword,
            string newPassword,
            string? confirmationCode = null,
            string? confirmationContext = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamOperationResult
            {
                Success = !string.IsNullOrWhiteSpace(sessionPayload) &&
                          !string.IsNullOrWhiteSpace(currentPassword) &&
                          !string.IsNullOrWhiteSpace(newPassword)
            });

        public Task<SteamOperationResult> DeauthorizeAllSessionsAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamOperationResult
            {
                Success = !string.IsNullOrWhiteSpace(sessionPayload),
                Data = new Dictionary<string, string> { ["deauthorized"] = true.ToString() }
            });

        public Task<SteamOwnedGamesSnapshot> GetOwnedGamesSnapshotAsync(string sessionPayload, CancellationToken cancellationToken = default)
        {
            if (Snapshots.TryGetValue(sessionPayload, out var snapshot))
            {
                return Task.FromResult(snapshot);
            }

            return Task.FromResult(new SteamOwnedGamesSnapshot
            {
                ProfileUrl = "https://steamcommunity.com/profiles/76561198000000000",
                Games = []
            });
        }

        public Task<SteamFriendInviteLink> GetFriendInviteLinkAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamFriendInviteLink
            {
                InviteUrl = "https://s.team/p/gqqn-cdpj/testtoken",
                InviteCode = "gqqn-cdpj",
                InviteToken = "testtoken",
                SyncedAt = DateTimeOffset.UtcNow
            });

        public Task<SteamOperationResult> AcceptFriendInviteAsync(string sessionPayload, string inviteUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamOperationResult
            {
                Success = !string.IsNullOrWhiteSpace(sessionPayload) && !string.IsNullOrWhiteSpace(inviteUrl),
                Data = new Dictionary<string, string>
                {
                    ["inviteUrl"] = inviteUrl
                }
            });

        public Task<SteamFriendsSnapshot> GetFriendsSnapshotAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamFriendsSnapshot
            {
                SyncedAt = DateTimeOffset.UtcNow,
                Friends = []
            });

        public Task<string?> ResolveSteamIdAsync(string loginName, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("76561198000000000");
    }
}
