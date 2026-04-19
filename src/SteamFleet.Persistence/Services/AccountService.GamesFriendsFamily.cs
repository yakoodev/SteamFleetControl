using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;

namespace SteamFleet.Persistence.Services;

public sealed partial class AccountService
{
    public async Task<AccountGamesPageResult> RefreshGamesAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .Include(x => x.Games)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        SteamOwnedGamesSnapshot snapshot;
        try
        {
            string sessionPayload;
            try
            {
                sessionPayload = await EnsureSessionPayloadAsync(account, actorId, ip, currentPassword: null, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                throw new SteamGatewayOperationException(
                    ex.Message,
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true,
                    ex);
            }

            snapshot = await steamGateway.GetOwnedGamesSnapshotAsync(sessionPayload, cancellationToken);
        }
        catch (SteamGatewayOperationException ex)
        {
            ApplyGatewayFailureState(account, ex.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        var existing = account.Games.ToDictionary(x => x.AppId);
        var incomingAppIds = new HashSet<int>();

        foreach (var game in snapshot.Games)
        {
            incomingAppIds.Add(game.AppId);
            if (existing.TryGetValue(game.AppId, out var row))
            {
                row.Name = game.Name;
                row.PlaytimeMinutes = game.PlaytimeMinutes;
                row.ImgIconUrl = game.ImgIconUrl;
                row.LastSyncedAt = snapshot.SyncedAt;
            }
            else
            {
                account.Games.Add(new SteamAccountGame
                {
                    AccountId = account.Id,
                    AppId = game.AppId,
                    Name = game.Name,
                    PlaytimeMinutes = game.PlaytimeMinutes,
                    ImgIconUrl = game.ImgIconUrl,
                    LastSyncedAt = snapshot.SyncedAt
                });
            }
        }

        var stale = account.Games.Where(x => !incomingAppIds.Contains(x.AppId)).ToList();
        if (stale.Count > 0)
        {
            dbContext.SteamAccountGames.RemoveRange(stale);
        }

        await PersistGamesSnapshotAsync(account, snapshot, actorId, cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.GamesRefreshed,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["games"] = snapshot.Games.Count.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        return await GetGamesAsync(account.Id, AccountGamesScope.Owned, null, 1, Math.Max(snapshot.Games.Count, 1), cancellationToken);
    }

    private async Task PersistGamesSnapshotAsync(
        SteamAccount account,
        SteamOwnedGamesSnapshot snapshot,
        string actorId,
        CancellationToken cancellationToken)
    {
        account.ProfileUrl = snapshot.ProfileUrl;
        account.GamesLastSyncAt = snapshot.SyncedAt;
        account.GamesCount = snapshot.Games.Count;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();

            var fresh = await dbContext.SteamAccounts
                .Include(x => x.Games)
                .FirstOrDefaultAsync(x => x.Id == account.Id, cancellationToken);
            if (fresh is null)
            {
                throw;
            }

            if (fresh.Games.Count > 0)
            {
                dbContext.SteamAccountGames.RemoveRange(fresh.Games);
            }

            foreach (var game in snapshot.Games)
            {
                dbContext.SteamAccountGames.Add(new SteamAccountGame
                {
                    AccountId = fresh.Id,
                    AppId = game.AppId,
                    Name = game.Name,
                    PlaytimeMinutes = game.PlaytimeMinutes,
                    ImgIconUrl = game.ImgIconUrl,
                    LastSyncedAt = snapshot.SyncedAt
                });
            }

            fresh.ProfileUrl = snapshot.ProfileUrl;
            fresh.GamesLastSyncAt = snapshot.SyncedAt;
            fresh.GamesCount = snapshot.Games.Count;
            fresh.LastSuccessAt = DateTimeOffset.UtcNow;
            fresh.LastErrorAt = null;
            fresh.Status = AccountStatus.Active;
            fresh.UpdatedBy = actorId;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<AccountGamesPageResult> GetGamesAsync(
        Guid id,
        AccountGamesScope scope,
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var rootId = account.ParentAccountId ?? account.Id;
        var familyAccounts = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => x.Id == rootId || x.ParentAccountId == rootId)
            .Select(x => new { x.Id, x.LoginName })
            .ToListAsync(cancellationToken);

        if (familyAccounts.Count == 0)
        {
            familyAccounts.Add(new { account.Id, account.LoginName });
        }

        var sourceAccountIds = scope switch
        {
            AccountGamesScope.Owned => new[] { account.Id },
            AccountGamesScope.Family => familyAccounts.Where(x => x.Id != account.Id).Select(x => x.Id).ToArray(),
            _ => familyAccounts.Select(x => x.Id).ToArray()
        };

        if (sourceAccountIds.Length == 0)
        {
            return new AccountGamesPageResult
            {
                Items = [],
                TotalCount = 0,
                Page = Math.Max(1, page),
                PageSize = Math.Clamp(pageSize, 1, 200)
            };
        }

        var gameQuery = dbContext.SteamAccountGames
            .AsNoTracking()
            .Where(x => sourceAccountIds.Contains(x.AccountId));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = query.Trim().ToLowerInvariant();
            gameQuery = gameQuery.Where(x => x.Name.ToLower().Contains(normalized));
        }

        var games = await gameQuery.ToListAsync(cancellationToken);
        var sourceLogins = familyAccounts.ToDictionary(x => x.Id, x => x.LoginName);

        var dedup = games
            .GroupBy(x => x.AppId)
            .Select(group =>
            {
                var ownRow = group.FirstOrDefault(x => x.AccountId == account.Id);
                var selected = ownRow ?? group
                    .OrderByDescending(x => x.PlaytimeMinutes)
                    .ThenBy(x => x.Name)
                    .First();

                var availability = selected.AccountId == account.Id
                    ? AccountGameAvailability.Owned
                    : AccountGameAvailability.FamilyGroup;

                return new AccountGameDto
                {
                    AppId = selected.AppId,
                    Name = selected.Name,
                    PlaytimeMinutes = selected.PlaytimeMinutes,
                    ImgIconUrl = selected.ImgIconUrl,
                    SourceAccountId = selected.AccountId,
                    SourceLoginName = sourceLogins.GetValueOrDefault(selected.AccountId, selected.AccountId.ToString()),
                    Availability = availability,
                    LastSyncedAt = selected.LastSyncedAt
                };
            })
            .OrderBy(x => x.Name)
            .ToList();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var skip = (page - 1) * pageSize;

        return new AccountGamesPageResult
        {
            TotalCount = dedup.Count,
            Page = page,
            PageSize = pageSize,
            Items = dedup.Skip(skip).Take(pageSize).ToArray()
        };
    }

    public async Task<FriendInviteLinkDto?> GetFriendInviteLinkAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (account is null)
        {
            return null;
        }

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        return BuildFriendInviteLinkDto(account.Id, metadata);
    }

    public async Task<FriendInviteLinkDto> SyncFriendInviteLinkAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        SteamFriendInviteLink invite;
        try
        {
            var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                account,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: false,
                cancellationToken);

            try
            {
                invite = await steamGateway.GetFriendInviteLinkAsync(sessionPayload, cancellationToken);
            }
            catch (SteamGatewayOperationException ex) when (ShouldForceReauthRetry(ex.ReasonCode, ex.Retryable))
            {
                sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                    account,
                    actorId,
                    ip,
                    currentPassword: null,
                    forceReauthenticate: true,
                    cancellationToken);
                invite = await steamGateway.GetFriendInviteLinkAsync(sessionPayload, cancellationToken);
            }
        }
        catch (SteamGatewayOperationException ex)
        {
            ApplyGatewayFailureState(account, ex.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        metadata["friends.invite.url"] = invite.InviteUrl;
        metadata["friends.invite.code"] = invite.InviteCode;
        metadata["friends.invite.token"] = invite.InviteToken;
        metadata["friends.invite.syncedAt"] = invite.SyncedAt.ToString("O", CultureInfo.InvariantCulture);

        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FriendInviteSynced,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["inviteCode"] = invite.InviteCode
            },
            cancellationToken);

        return BuildFriendInviteLinkDto(account.Id, metadata);
    }

    public async Task<SteamOperationResult> AcceptFriendInviteAsync(
        Guid id,
        string inviteUrl,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        if (string.IsNullOrWhiteSpace(inviteUrl))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Invite URL is required.",
                ReasonCode = SteamReasonCodes.InvalidInviteLink
            };
        }

        var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
            account,
            actorId,
            ip,
            currentPassword: null,
            forceReauthenticate: false,
            cancellationToken);
        var result = await steamGateway.AcceptFriendInviteAsync(sessionPayload, inviteUrl, cancellationToken);
        if (!result.Success && ShouldForceReauthRetry(result.ReasonCode, result.Retryable))
        {
            sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                account,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: true,
                cancellationToken);
            result = await steamGateway.AcceptFriendInviteAsync(sessionPayload, inviteUrl, cancellationToken);
        }

        if (!result.Success)
        {
            ApplyGatewayFailureState(account, result.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);

            await auditService.WriteAsync(
                AuditEventType.FriendConnectFailed,
                "steam_account",
                account.Id.ToString(),
                actorId,
                ip,
                new Dictionary<string, string>
                {
                    ["reasonCode"] = result.ReasonCode ?? SteamReasonCodes.Unknown,
                    ["retryable"] = result.Retryable.ToString()
                },
                cancellationToken);

            return result;
        }

        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        if (account.Status is AccountStatus.Error or AccountStatus.RequiresRelogin)
        {
            account.Status = AccountStatus.Active;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FriendInviteAccepted,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            cancellationToken: cancellationToken);

        return result;
    }

    public async Task<AccountFriendsSnapshotDto> RefreshFriendsAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
            account,
            actorId,
            ip,
            currentPassword: null,
            forceReauthenticate: false,
            cancellationToken);
        SteamFriendsSnapshot snapshot;
        try
        {
            snapshot = await steamGateway.GetFriendsSnapshotAsync(sessionPayload, cancellationToken);
        }
        catch (SteamGatewayOperationException ex) when (ShouldForceReauthRetry(ex.ReasonCode, ex.Retryable))
        {
            sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                account,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: true,
                cancellationToken);
            snapshot = await steamGateway.GetFriendsSnapshotAsync(sessionPayload, cancellationToken);
        }

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        var friendsJson = JsonSerializer.Serialize(snapshot.Friends, JsonSerialization.Defaults);
        metadata["friends.snapshot"] = friendsJson;
        metadata["friends.syncedAt"] = snapshot.SyncedAt.ToString("O", CultureInfo.InvariantCulture);

        account.MetadataJson = JsonSerialization.SerializeDictionary(metadata);
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["operation"] = "friends_refresh",
                ["friends"] = snapshot.Friends.Count.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        return BuildFriendsSnapshotDto(account.Id, metadata);
    }

    public async Task<AccountFriendsSnapshotDto> GetFriendsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var metadata = JsonSerialization.DeserializeDictionary(account.MetadataJson);
        return BuildFriendsSnapshotDto(account.Id, metadata);
    }

    public async Task<AccountDto?> AssignParentAsync(
        Guid id,
        Guid parentAccountId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        if (id == parentAccountId)
        {
            throw new InvalidOperationException("Account cannot be parent of itself.");
        }

        var account = await dbContext.SteamAccounts
            .Include(x => x.ChildAccounts)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var parent = await dbContext.SteamAccounts
            .Include(x => x.ChildAccounts)
            .FirstOrDefaultAsync(x => x.Id == parentAccountId, cancellationToken)
            ?? throw new InvalidOperationException("Parent account not found.");

        if (account.ParentAccountId == parentAccountId)
        {
            return await GetByIdAsync(id, cancellationToken);
        }

        if (account.ChildAccounts.Count > 0)
        {
            throw new InvalidOperationException("Account that already has children cannot become a family child.");
        }

        if (parent.ParentAccountId is not null)
        {
            throw new InvalidOperationException("Selected parent account is already a child in another family group.");
        }

        account.ParentAccountId = parent.Id;
        account.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FamilyParentAssigned,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["parentAccountId"] = parent.Id.ToString(),
                ["parentLogin"] = parent.LoginName
            },
            cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<AccountDto?> RemoveParentAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (account is null)
        {
            return null;
        }

        if (account.ParentAccountId is null)
        {
            return await GetByIdAsync(id, cancellationToken);
        }

        account.ParentAccountId = null;
        account.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FamilyParentRemoved,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            cancellationToken: cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    private async Task<Dictionary<Guid, int>> BuildFamilyCountsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var roots = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.Id))
            .Select(x => x.ParentAccountId ?? x.Id)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        if (roots.Length == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var counts = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => roots.Contains(x.Id) || (x.ParentAccountId != null && roots.Contains(x.ParentAccountId.Value)))
            .GroupBy(x => x.ParentAccountId ?? x.Id)
            .Select(g => new { RootId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.RootId, x => x.Count);
    }

    private static FriendInviteLinkDto BuildFriendInviteLinkDto(Guid accountId, IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue("friends.invite.url", out var inviteUrl);
        metadata.TryGetValue("friends.invite.code", out var inviteCode);
        metadata.TryGetValue("friends.invite.syncedAt", out var syncedAtRaw);
        var syncedAt = TryParseDate(syncedAtRaw);

        return new FriendInviteLinkDto
        {
            AccountId = accountId,
            InviteUrl = inviteUrl,
            InviteCode = inviteCode,
            LastSyncedAt = syncedAt
        };
    }

    private static AccountFriendsSnapshotDto BuildFriendsSnapshotDto(Guid accountId, IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue("friends.syncedAt", out var syncedAtRaw);
        metadata.TryGetValue("friends.snapshot", out var friendsRaw);

        var items = Array.Empty<AccountFriendDto>();
        if (!string.IsNullOrWhiteSpace(friendsRaw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<SteamFriend>>(friendsRaw, JsonSerialization.Defaults) ?? [];
                items = parsed
                    .Select(x => new AccountFriendDto
                    {
                        SteamId64 = x.SteamId64,
                        PersonaName = x.PersonaName,
                        ProfileUrl = x.ProfileUrl
                    })
                    .OrderBy(x => x.PersonaName ?? x.SteamId64, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (JsonException)
            {
                items = [];
            }
        }

        return new AccountFriendsSnapshotDto
        {
            AccountId = accountId,
            LastSyncedAt = TryParseDate(syncedAtRaw),
            Friends = items
        };
    }
}

