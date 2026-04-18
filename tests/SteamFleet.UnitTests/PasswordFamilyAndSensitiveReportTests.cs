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

namespace SteamFleet.UnitTests;

public sealed class PasswordFamilyAndSensitiveReportTests
{
    [Fact]
    public async Task PasswordChangeJob_GeneratesUniquePasswords_And_SensitiveReportIsOneTime()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var gateway = new FakeSteamGateway();
        var jobService = new JobService(dbContext, gateway, crypto, auditService);

        var accounts = new[]
        {
            CreateAccount("acc-one", crypto),
            CreateAccount("acc-two", crypto),
            CreateAccount("acc-three", crypto)
        };

        await dbContext.SteamAccounts.AddRangeAsync(accounts);
        await dbContext.SaveChangesAsync();

        var job = await jobService.CreateAsync(new JobCreateRequest
        {
            Type = JobType.PasswordChange,
            AccountIds = accounts.Select(x => x.Id).ToList(),
            DryRun = false,
            Parallelism = 3,
            RetryCount = 1,
            Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generateLength"] = "20",
                ["deauthorizeAfterChange"] = "false"
            }
        }, "unit-test", "127.0.0.1");

        await jobService.ExecuteAsync(job.Id);

        var done = await jobService.GetByIdAsync(job.Id);
        Assert.NotNull(done);
        Assert.True(done!.Status is JobStatus.Completed or JobStatus.Failed);
        Assert.Equal(3, done.SuccessCount);
        Assert.Equal(0, done.FailureCount);
        Assert.True(done.HasSensitiveReport);
        Assert.False(done.SensitiveReportConsumed);

        var firstDownload = await jobService.DownloadSensitiveReportOnceAsync(job.Id, "unit-test", "127.0.0.1");
        Assert.NotNull(firstDownload);
        var csv = Encoding.UTF8.GetString(firstDownload!);
        var lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("accountId,login,newPassword,deauthorized", lines[0]);

        var passwords = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            Assert.Equal(4, parts.Length);
            var nextPassword = parts[2];
            passwords.Add(nextPassword);

            Assert.Equal(20, nextPassword.Length);
            Assert.Contains(nextPassword, char.IsUpper);
            Assert.Contains(nextPassword, char.IsLower);
            Assert.Contains(nextPassword, char.IsDigit);
            Assert.Contains(nextPassword, c => "!@#$%*_-+=".Contains(c));
        }

        Assert.Equal(passwords.Count, passwords.Distinct(StringComparer.Ordinal).Count());

        var items = await jobService.GetItemsAsync(job.Id);
        foreach (var item in items)
        {
            Assert.False(item.Result.ContainsKey("newPassword"));
            var resultDump = string.Join('|', item.Result.Select(x => $"{x.Key}={x.Value}"));
            foreach (var password in passwords)
            {
                Assert.DoesNotContain(password, resultDump);
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jobService.DownloadSensitiveReportOnceAsync(job.Id, "unit-test", "127.0.0.1"));

        var auditEvents = await auditService.GetAsync(0, 1000);
        Assert.Equal(3, auditEvents.Count(x => x.EventType == AuditEventType.PasswordChanged));
        Assert.Contains(auditEvents, x => x.EventType == AuditEventType.SensitiveReportDownloaded);
    }

    [Fact]
    public async Task FamilyRules_EnforceSelfAndOneLevelConstraints()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var accountService = new AccountService(dbContext, new FakeSteamGateway(), crypto, auditService);

        var root = CreateAccount("root", crypto);
        var child = CreateAccount("child", crypto);
        var leaf = CreateAccount("leaf", crypto);
        var otherParent = CreateAccount("other-parent", crypto);
        var otherChild = CreateAccount("other-child", crypto);

        await dbContext.SteamAccounts.AddRangeAsync(root, child, leaf, otherParent, otherChild);
        await dbContext.SaveChangesAsync();

        var firstAssign = await accountService.AssignParentAsync(child.Id, root.Id, "unit-test", "127.0.0.1");
        Assert.NotNull(firstAssign);
        Assert.Equal(root.Id, firstAssign!.ParentAccountId);

        var secondAssign = await accountService.AssignParentAsync(otherChild.Id, otherParent.Id, "unit-test", "127.0.0.1");
        Assert.NotNull(secondAssign);
        Assert.Equal(otherParent.Id, secondAssign!.ParentAccountId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accountService.AssignParentAsync(root.Id, root.Id, "unit-test", "127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accountService.AssignParentAsync(root.Id, child.Id, "unit-test", "127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accountService.AssignParentAsync(otherParent.Id, root.Id, "unit-test", "127.0.0.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accountService.AssignParentAsync(leaf.Id, child.Id, "unit-test", "127.0.0.1"));
    }

    [Fact]
    public async Task GetGamesAsync_ReturnsOwnedAndFamilyAvailability()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var accountService = new AccountService(dbContext, new FakeSteamGateway(), crypto, new AuditService(dbContext));

        var parent = CreateAccount("main-account", crypto);
        var child = CreateAccount("family-child", crypto);
        child.ParentAccountId = parent.Id;

        await dbContext.SteamAccounts.AddRangeAsync(parent, child);
        await dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        await dbContext.SteamAccountGames.AddRangeAsync(
            new SteamAccountGame
            {
                AccountId = parent.Id,
                AppId = 10,
                Name = "Counter-Strike",
                PlaytimeMinutes = 120,
                LastSyncedAt = now
            },
            new SteamAccountGame
            {
                AccountId = parent.Id,
                AppId = 20,
                Name = "Team Fortress Classic",
                PlaytimeMinutes = 50,
                LastSyncedAt = now
            },
            new SteamAccountGame
            {
                AccountId = child.Id,
                AppId = 20,
                Name = "Team Fortress Classic",
                PlaytimeMinutes = 300,
                LastSyncedAt = now
            },
            new SteamAccountGame
            {
                AccountId = child.Id,
                AppId = 30,
                Name = "Day of Defeat",
                PlaytimeMinutes = 10,
                LastSyncedAt = now
            });
        await dbContext.SaveChangesAsync();

        var all = await accountService.GetGamesAsync(child.Id, AccountGamesScope.All, null, 1, 100);
        Assert.Equal(3, all.TotalCount);

        var app20 = Assert.Single(all.Items, x => x.AppId == 20);
        Assert.Equal(AccountGameAvailability.Owned, app20.Availability);
        Assert.Equal(child.Id, app20.SourceAccountId);

        var app10 = Assert.Single(all.Items, x => x.AppId == 10);
        Assert.Equal(AccountGameAvailability.FamilyGroup, app10.Availability);
        Assert.Equal(parent.Id, app10.SourceAccountId);

        var familyOnly = await accountService.GetGamesAsync(child.Id, AccountGamesScope.Family, null, 1, 100);
        Assert.Equal(2, familyOnly.TotalCount);
        Assert.All(familyOnly.Items, x => Assert.Equal(AccountGameAvailability.FamilyGroup, x.Availability));

        var ownedOnly = await accountService.GetGamesAsync(child.Id, AccountGamesScope.Owned, null, 1, 100);
        Assert.Equal(2, ownedOnly.TotalCount);
        Assert.All(ownedOnly.Items, x => Assert.Equal(AccountGameAvailability.Owned, x.Availability));
    }

    [Fact]
    public async Task ChangePassword_DeauthorizeFailure_DoesNotRollbackPasswordChange()
    {
        await using var dbContext = CreateDbContext();
        var crypto = CreateCrypto();
        var auditService = new AuditService(dbContext);
        var gateway = new FakeSteamGateway(
            deauthorizeSuccess: false,
            deauthorizeErrorMessage: "Steam did not confirm deauthorization.",
            deauthorizeReasonCode: SteamReasonCodes.AuthSessionMissing,
            deauthorizeRetryable: true);
        var accountService = new AccountService(dbContext, gateway, crypto, auditService);

        var account = CreateAccount("change-pass-user", crypto);
        await dbContext.SteamAccounts.AddAsync(account);
        await dbContext.SaveChangesAsync();

        var newPassword = "NewStrongPass1!";
        var result = await accountService.ChangePasswordAsync(
            account.Id,
            new AccountPasswordChangeRequest
            {
                NewPassword = newPassword,
                GenerateIfEmpty = false,
                DeauthorizeAfterChange = true
            },
            "unit-test",
            "127.0.0.1");

        Assert.True(result.Success);
        Assert.Equal(newPassword, result.NewPassword);
        Assert.False(result.Deauthorized);
        Assert.Contains("Пароль изменён", result.ErrorMessage);
        Assert.Equal(SteamReasonCodes.AuthSessionMissing, result.ReasonCode);
        Assert.True(result.Retryable);

        var refreshed = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstAsync(x => x.Id == account.Id);
        Assert.NotNull(refreshed.Secret);
        var storedPassword = crypto.Decrypt(refreshed.Secret!.EncryptedPassword);
        Assert.Equal(newPassword, storedPassword);
        Assert.Equal(AccountStatus.Active, refreshed.Status);
    }

    private static SteamFleetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SteamFleetDbContext>()
            .UseInMemoryDatabase($"steamfleet-unit-{Guid.NewGuid():N}")
            .Options;
        return new SteamFleetDbContext(options);
    }

    private static AesGcmSecretCryptoService CreateCrypto()
    {
        var keyBytes = new byte[32];
        Random.Shared.NextBytes(keyBytes);
        return new AesGcmSecretCryptoService(Convert.ToBase64String(keyBytes));
    }

    private static SteamAccount CreateAccount(string loginName, ISecretCryptoService crypto)
    {
        var account = new SteamAccount
        {
            LoginName = loginName,
            DisplayName = loginName,
            Status = AccountStatus.Active
        };

        account.Secret = new SteamAccountSecret
        {
            AccountId = account.Id,
            EncryptedPassword = crypto.Encrypt("CurrentPass1!"),
            EncryptedSessionPayload = crypto.Encrypt($"session::{loginName}"),
            EncryptionVersion = crypto.Version
        };

        return account;
    }

    private sealed class FakeSteamGateway : ISteamAccountGateway
    {
        private readonly bool _deauthorizeSuccess;
        private readonly string? _deauthorizeErrorMessage;
        private readonly string? _deauthorizeReasonCode;
        private readonly bool _deauthorizeRetryable;

        public FakeSteamGateway(
            bool deauthorizeSuccess = true,
            string? deauthorizeErrorMessage = null,
            string? deauthorizeReasonCode = null,
            bool deauthorizeRetryable = false)
        {
            _deauthorizeSuccess = deauthorizeSuccess;
            _deauthorizeErrorMessage = deauthorizeErrorMessage;
            _deauthorizeReasonCode = deauthorizeReasonCode;
            _deauthorizeRetryable = deauthorizeRetryable;
        }

        public Task<SteamAuthResult> AuthenticateAsync(SteamCredentials credentials, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SteamAuthResult
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
        }

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
                Status = SteamQrAuthStatus.Completed,
                AuthResult = new SteamAuthResult
                {
                    Success = true,
                    SteamId64 = "76561198000000000",
                    Session = new SteamSessionInfo { CookiePayload = "session::qr" }
                },
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
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
                Success = _deauthorizeSuccess && !string.IsNullOrWhiteSpace(sessionPayload),
                ErrorMessage = _deauthorizeSuccess ? null : _deauthorizeErrorMessage,
                ReasonCode = _deauthorizeSuccess ? null : _deauthorizeReasonCode,
                Retryable = !_deauthorizeSuccess && _deauthorizeRetryable,
                Data = _deauthorizeSuccess
                    ? new Dictionary<string, string> { ["deauthorized"] = true.ToString() }
                    : new Dictionary<string, string>()
            });

        public Task<SteamOwnedGamesSnapshot> GetOwnedGamesSnapshotAsync(string sessionPayload, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamOwnedGamesSnapshot
            {
                ProfileUrl = "https://steamcommunity.com/profiles/76561198000000000",
                Games = []
            });

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
