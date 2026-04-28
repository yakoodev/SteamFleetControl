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
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .Include(x => x.Games)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

        if (account.IsExternal)
        {
            throw new InvalidOperationException("External family member cannot refresh owned games by session.");
        }

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
            var transition = ApplyGatewayFailureState(account, ex.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                ex.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);
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
        await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

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

        var familyAccounts = string.IsNullOrWhiteSpace(account.SteamFamilyId)
            ? [new { account.Id, account.LoginName }]
            : await dbContext.SteamAccounts
                .AsNoTracking()
                .Where(x => x.SteamFamilyId == account.SteamFamilyId)
                .Select(x => new { x.Id, x.LoginName })
                .ToListAsync(cancellationToken);

        var sourceAccountIds = scope switch
        {
            AccountGamesScope.Owned => [account.Id],
            AccountGamesScope.Family => familyAccounts.Where(x => x.Id != account.Id).Select(x => x.Id).Distinct().ToArray(),
            _ => familyAccounts.Select(x => x.Id).Distinct().ToArray()
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

    public async Task<AccountFamilySnapshotDto> SyncFamilyFromSteamAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

        if (account.IsExternal)
        {
            throw new InvalidOperationException("External family member cannot be used as a sync source.");
        }

        var previousFamilyId = account.SteamFamilyId;
        var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
            account,
            actorId,
            ip,
            currentPassword: null,
            forceReauthenticate: false,
            cancellationToken);

        SteamFamilySnapshot familySnapshot;
        try
        {
            try
            {
                familySnapshot = await steamGateway.GetFamilySnapshotAsync(sessionPayload, cancellationToken);
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
                familySnapshot = await steamGateway.GetFamilySnapshotAsync(sessionPayload, cancellationToken);
            }
        }
        catch (SteamGatewayOperationException ex)
        {
            var transition = ApplyGatewayFailureState(account, ex.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                ex.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);
            throw;
        }

        var familyId = familySnapshot.FamilyId?.Trim();
        if (string.IsNullOrWhiteSpace(familyId))
        {
            account.SteamFamilyId = null;
            account.SteamFamilyRole = null;
            account.IsFamilyOrganizer = false;
            account.FamilySyncedAt = familySnapshot.SyncedAt;
            account.UpdatedBy = actorId;
            await dbContext.SaveChangesAsync(cancellationToken);

            await auditService.WriteAsync(
                AuditEventType.FamilySynced,
                "steam_account",
                account.Id.ToString(),
                actorId,
                ip,
                new Dictionary<string, string>
                {
                    ["familyId"] = "none",
                    ["members"] = "0"
                },
                cancellationToken);

            return await GetFamilySnapshotAsync(id, cancellationToken);
        }

        var members = familySnapshot.Members
            .Where(x => !string.IsNullOrWhiteSpace(x.SteamId64))
            .GroupBy(x => x.SteamId64.Trim(), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (!string.IsNullOrWhiteSpace(account.SteamId64) &&
            members.All(x => !string.Equals(x.SteamId64, account.SteamId64, StringComparison.Ordinal)))
        {
            members.Add(new SteamFamilyMember
            {
                SteamId64 = account.SteamId64,
                DisplayName = account.DisplayName,
                Role = familySnapshot.SelfRole,
                IsOrganizer = familySnapshot.IsOrganizer
            });
        }

        var memberSteamIds = members
            .Select(x => x.SteamId64.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var knownAccounts = await dbContext.SteamAccounts
            .Where(x => x.SteamId64 != null && memberSteamIds.Contains(x.SteamId64))
            .ToDictionaryAsync(x => x.SteamId64!, x => x, StringComparer.Ordinal, cancellationToken);

        foreach (var member in members)
        {
            var steamId64 = member.SteamId64.Trim();
            if (!knownAccounts.TryGetValue(steamId64, out var memberAccount))
            {
                var loginName = await GenerateUniqueExternalLoginNameAsync(steamId64, cancellationToken);
                memberAccount = new SteamAccount
                {
                    LoginName = loginName,
                    SteamId64 = steamId64,
                    DisplayName = member.DisplayName ?? $"Steam {steamId64}",
                    IsExternal = true,
                    ExternalSource = "SteamFamily",
                    Status = AccountStatus.Active,
                    CreatedBy = actorId,
                    UpdatedBy = actorId
                };
                await dbContext.SteamAccounts.AddAsync(memberAccount, cancellationToken);
                knownAccounts[steamId64] = memberAccount;

                await auditService.WriteAsync(
                    AuditEventType.ExternalFamilyMemberUpserted,
                    "steam_account",
                    memberAccount.Id.ToString(),
                    actorId,
                    ip,
                    new Dictionary<string, string>
                    {
                        ["steamId64"] = steamId64,
                        ["familyId"] = familyId
                    },
                    cancellationToken);
            }

            memberAccount.SteamFamilyId = familyId;
            memberAccount.SteamFamilyRole = member.Role;
            memberAccount.IsFamilyOrganizer = member.IsOrganizer;
            memberAccount.FamilySyncedAt = familySnapshot.SyncedAt;
            memberAccount.UpdatedBy = actorId;
            if (!string.IsNullOrWhiteSpace(member.DisplayName))
            {
                memberAccount.DisplayName = member.DisplayName;
            }

            if (memberAccount.IsExternal)
            {
                memberAccount.ExternalSource = "SteamFamily";
                try
                {
                    var publicData = await steamGateway.GetPublicMemberDataAsync(steamId64, cancellationToken);
                    memberAccount.ProfileUrl = publicData.ProfileUrl ?? memberAccount.ProfileUrl;
                    if (!string.IsNullOrWhiteSpace(publicData.DisplayName))
                    {
                        memberAccount.DisplayName = publicData.DisplayName;
                    }

                    await UpsertGamesForAccountAsync(memberAccount, publicData.Games, publicData.SyncedAt, cancellationToken);
                }
                catch (SteamGatewayOperationException ex) when (
                    string.Equals(ex.ReasonCode, SteamReasonCodes.ExternalDataUnavailable, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ex.ReasonCode, SteamReasonCodes.EndpointRejected, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ex.ReasonCode, SteamReasonCodes.Unknown, StringComparison.OrdinalIgnoreCase))
                {
                    // Best-effort open-data enrichment must not fail the full family sync.
                    memberAccount.LastErrorAt = DateTimeOffset.UtcNow;
                }
            }
        }

        var cleanupFamilyIds = new List<string> { familyId };
        if (!string.IsNullOrWhiteSpace(previousFamilyId) &&
            !string.Equals(previousFamilyId, familyId, StringComparison.Ordinal))
        {
            cleanupFamilyIds.Add(previousFamilyId);
        }

        var staleAccounts = await dbContext.SteamAccounts
            .Where(x => x.SteamFamilyId != null && cleanupFamilyIds.Contains(x.SteamFamilyId))
            .ToListAsync(cancellationToken);
        foreach (var stale in staleAccounts)
        {
            if (string.IsNullOrWhiteSpace(stale.SteamId64) || memberSteamIds.Contains(stale.SteamId64))
            {
                continue;
            }

            stale.SteamFamilyId = null;
            stale.SteamFamilyRole = null;
            stale.IsFamilyOrganizer = false;
            stale.FamilySyncedAt = familySnapshot.SyncedAt;
            stale.UpdatedBy = actorId;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FamilySynced,
            "steam_account",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["familyId"] = familyId,
                ["members"] = memberSteamIds.Length.ToString(CultureInfo.InvariantCulture),
                ["externalMembers"] = members.Count(x => knownAccounts.TryGetValue(x.SteamId64.Trim(), out var row) && row.IsExternal)
                    .ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);
        await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetFamilySnapshotAsync(id, cancellationToken);
    }

    public async Task<AccountFamilySnapshotDto> GetFamilySnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        if (string.IsNullOrWhiteSpace(account.SteamFamilyId))
        {
            return new AccountFamilySnapshotDto
            {
                AccountId = account.Id,
                SteamFamilyId = null,
                LastSyncedAt = account.FamilySyncedAt,
                IsOrganizer = account.IsFamilyOrganizer,
                SelfRole = account.SteamFamilyRole,
                Members =
                [
                    new AccountFamilyMemberDto
                    {
                        AccountId = account.Id,
                        SteamId64 = account.SteamId64 ?? string.Empty,
                        LoginName = account.LoginName,
                        DisplayName = account.DisplayName,
                        IsExternal = account.IsExternal,
                        ExternalSource = account.ExternalSource,
                        ProfileUrl = account.ProfileUrl,
                        FamilyRole = account.SteamFamilyRole,
                        IsOrganizer = account.IsFamilyOrganizer,
                        GamesCount = account.GamesCount,
                        GamesLastSyncAt = account.GamesLastSyncAt
                    }
                ]
            };
        }

        var members = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => x.SteamFamilyId == account.SteamFamilyId)
            .OrderBy(x => x.IsExternal)
            .ThenBy(x => x.LoginName)
            .Select(x => new AccountFamilyMemberDto
            {
                AccountId = x.Id,
                SteamId64 = x.SteamId64 ?? string.Empty,
                LoginName = x.LoginName,
                DisplayName = x.DisplayName,
                IsExternal = x.IsExternal,
                ExternalSource = x.ExternalSource,
                ProfileUrl = x.ProfileUrl,
                FamilyRole = x.SteamFamilyRole,
                IsOrganizer = x.IsFamilyOrganizer,
                GamesCount = x.GamesCount,
                GamesLastSyncAt = x.GamesLastSyncAt
            })
            .ToArrayAsync(cancellationToken);

        return new AccountFamilySnapshotDto
        {
            AccountId = account.Id,
            SteamFamilyId = account.SteamFamilyId,
            LastSyncedAt = account.FamilySyncedAt,
            IsOrganizer = account.IsFamilyOrganizer,
            SelfRole = account.SteamFamilyRole,
            Members = members
        };
    }

    public async Task<SteamOperationResult> InviteToFamilyAsync(
        Guid id,
        FamilyInviteRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        if (request.TargetAccountId == Guid.Empty)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Target account id is required.",
                ReasonCode = SteamReasonCodes.TargetAccountMissing
            };
        }

        if (request.TargetAccountId == id)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Source and target accounts must differ.",
                ReasonCode = SteamReasonCodes.EndpointRejected
            };
        }

        var organizer = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(organizer, automated: false, cancellationToken);

        if (organizer.IsExternal)
        {
            throw new InvalidOperationException("External account cannot invite members to Steam Family.");
        }

        var target = await dbContext.SteamAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.TargetAccountId, cancellationToken)
            ?? throw new InvalidOperationException("Target account not found.");

        if (target.IsExternal)
        {
            throw new InvalidOperationException("External account cannot be used as an invite target.");
        }

        if (string.IsNullOrWhiteSpace(target.SteamId64))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Target account does not have SteamId64.",
                ReasonCode = SteamReasonCodes.TargetAccountMissing
            };
        }

        var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
            organizer,
            actorId,
            ip,
            currentPassword: null,
            forceReauthenticate: false,
            cancellationToken);

        var result = await steamGateway.InviteToFamilyGroupAsync(
            sessionPayload,
            target.SteamId64,
            request.InviteAsChild,
            cancellationToken);

        if (!result.Success && ShouldForceReauthRetry(result.ReasonCode, result.Retryable))
        {
            sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                organizer,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: true,
                cancellationToken);
            result = await steamGateway.InviteToFamilyGroupAsync(
                sessionPayload,
                target.SteamId64,
                request.InviteAsChild,
                cancellationToken);
        }

        if (!result.Success &&
            string.Equals(result.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
        {
            var autoAccepted = await TryAutoAcceptGuardConfirmationsAsync(
                organizer,
                sessionPayload,
                actorId,
                ip,
                cancellationToken,
                operation: "family_invite_send",
                relevanceKeywords: ["family", "invite", "household"],
                expectedCreatorSteamIds: [organizer.SteamId64, target.SteamId64]);
            if (autoAccepted)
            {
                result = await steamGateway.InviteToFamilyGroupAsync(
                    sessionPayload,
                    target.SteamId64,
                    request.InviteAsChild,
                    cancellationToken);
            }
        }

        if (!result.Success)
        {
            var transition = ApplyGatewayFailureState(organizer, result.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                organizer,
                transition,
                actorId,
                ip,
                result.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);

            await auditService.WriteAsync(
                AuditEventType.FamilyInviteFailed,
                "steam_account",
                organizer.Id.ToString(),
                actorId,
                ip,
                new Dictionary<string, string>
                {
                    ["targetAccountId"] = target.Id.ToString(),
                    ["targetSteamId64"] = target.SteamId64,
                    ["reasonCode"] = result.ReasonCode ?? SteamReasonCodes.Unknown,
                    ["retryable"] = result.Retryable.ToString()
                },
                cancellationToken);

            return result;
        }

        organizer.LastSuccessAt = DateTimeOffset.UtcNow;
        organizer.LastErrorAt = null;
        if (organizer.Status is AccountStatus.Error or AccountStatus.RequiresRelogin)
        {
            organizer.Status = AccountStatus.Active;
        }

        organizer.UpdatedBy = actorId;
        await MarkRiskRecoveredAsync(organizer, actorId, ip, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.FamilyInviteSent,
            "steam_account",
            organizer.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["targetAccountId"] = target.Id.ToString(),
                ["targetSteamId64"] = target.SteamId64,
                ["inviteRole"] = request.InviteAsChild ? "Child" : "Adult"
            },
            cancellationToken);

        return result;
    }

    public async Task<SteamOperationResult> AcceptFamilyInviteAsync(
        Guid id,
        FamilyAcceptInviteRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        SteamOperationResult result;
        string? sourceSteamId64 = null;

        await using (var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken))
        {
            var account = await dbContext.SteamAccounts
                .Include(x => x.Secret)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Account not found.");
            await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

            if (account.IsExternal)
            {
                throw new InvalidOperationException("External account cannot accept Steam Family invites.");
            }

            if (request.SourceAccountId.HasValue)
            {
                if (request.SourceAccountId.Value == id)
                {
                    return new SteamOperationResult
                    {
                        Success = false,
                        ErrorMessage = "Source and target accounts must differ.",
                        ReasonCode = SteamReasonCodes.EndpointRejected
                    };
                }

                var source = await dbContext.SteamAccounts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == request.SourceAccountId.Value, cancellationToken)
                    ?? throw new InvalidOperationException("Source account not found.");

                if (string.IsNullOrWhiteSpace(source.SteamId64))
                {
                    return new SteamOperationResult
                    {
                        Success = false,
                        ErrorMessage = "Source account does not have SteamId64.",
                        ReasonCode = SteamReasonCodes.SourceAccountMissing
                    };
                }

                sourceSteamId64 = source.SteamId64;
            }

            var sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                account,
                actorId,
                ip,
                currentPassword: null,
                forceReauthenticate: false,
                cancellationToken);

            result = await steamGateway.AcceptFamilyInviteAsync(
                sessionPayload,
                sourceSteamId64,
                cancellationToken);

            if (!result.Success && ShouldForceReauthRetry(result.ReasonCode, result.Retryable))
            {
                sessionPayload = await EnsureSessionPayloadForGatewayAsync(
                    account,
                    actorId,
                    ip,
                    currentPassword: null,
                    forceReauthenticate: true,
                    cancellationToken);
                result = await steamGateway.AcceptFamilyInviteAsync(
                    sessionPayload,
                    sourceSteamId64,
                    cancellationToken);
            }

            if (!result.Success &&
                string.Equals(result.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
            {
                var autoAccepted = await TryAutoAcceptGuardConfirmationsAsync(
                    account,
                    sessionPayload,
                    actorId,
                    ip,
                    cancellationToken,
                    operation: "family_invite_accept",
                    relevanceKeywords: ["family", "invite", "household"],
                    expectedCreatorSteamIds: [account.SteamId64, sourceSteamId64]);
                if (autoAccepted)
                {
                    result = await steamGateway.AcceptFamilyInviteAsync(
                        sessionPayload,
                        sourceSteamId64,
                        cancellationToken);
                }
            }

            if (!result.Success)
            {
                var transition = ApplyGatewayFailureState(account, result.ReasonCode, actorId);
                await dbContext.SaveChangesAsync(cancellationToken);
                await WriteRiskAuditAsync(
                    account,
                    transition,
                    actorId,
                    ip,
                    result.ReasonCode ?? SteamReasonCodes.Unknown,
                    cancellationToken);

                await auditService.WriteAsync(
                    AuditEventType.FamilyInviteFailed,
                    "steam_account",
                    account.Id.ToString(),
                    actorId,
                    ip,
                    new Dictionary<string, string>
                    {
                        ["sourceAccountId"] = request.SourceAccountId?.ToString() ?? string.Empty,
                        ["sourceSteamId64"] = sourceSteamId64 ?? string.Empty,
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

            account.UpdatedBy = actorId;
            await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            await auditService.WriteAsync(
                AuditEventType.FamilyInviteAccepted,
                "steam_account",
                account.Id.ToString(),
                actorId,
                ip,
                new Dictionary<string, string>
                {
                    ["sourceAccountId"] = request.SourceAccountId?.ToString() ?? string.Empty,
                    ["sourceSteamId64"] = sourceSteamId64 ?? string.Empty
                },
                cancellationToken);
        }

        try
        {
            await SyncFamilyFromSteamAsync(id, actorId, ip, cancellationToken);
        }
        catch
        {
            // Accept operation stays successful even if immediate family-sync failed.
        }

        if (request.SourceAccountId.HasValue)
        {
            try
            {
                await SyncFamilyFromSteamAsync(request.SourceAccountId.Value, actorId, ip, cancellationToken);
            }
            catch
            {
                // Best-effort sync for source account.
            }
        }

        return result;
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
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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
            var transition = ApplyGatewayFailureState(account, ex.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                ex.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);
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
        await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
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
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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
            var transition = ApplyGatewayFailureState(account, result.ReasonCode, actorId);
            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRiskAuditAsync(
                account,
                transition,
                actorId,
                ip,
                result.ReasonCode ?? SteamReasonCodes.Unknown,
                cancellationToken);

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

        await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);
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
        await using var operationLease = await AcquireAccountOperationLockAsync(id, cancellationToken);
        var account = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");
        await ApplySensitivePrecheckAsync(account, automated: false, cancellationToken);

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
        await MarkRiskRecoveredAsync(account, actorId, ip, cancellationToken);

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

    public Task<AccountDto?> AssignParentAsync(
        Guid id,
        Guid parentAccountId,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AccountDto?>(
            new InvalidOperationException("Manual family assignment is disabled. Use /family/sync."));

    public Task<AccountDto?> RemoveParentAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AccountDto?>(
            new InvalidOperationException("Manual family assignment is disabled. Use /family/sync."));

    private async Task<Dictionary<string, int>> BuildFamilyCountsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var familyKeys = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.Id))
            .Select(x => x.SteamFamilyId ?? x.Id.ToString())
            .Distinct()
            .ToArrayAsync(cancellationToken);

        if (familyKeys.Length == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var counts = await dbContext.SteamAccounts
            .AsNoTracking()
            .Where(x => familyKeys.Contains(x.SteamFamilyId ?? x.Id.ToString()))
            .GroupBy(x => x.SteamFamilyId ?? x.Id.ToString())
            .Select(g => new { FamilyKey = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.FamilyKey, x => x.Count, StringComparer.Ordinal);
    }

    private async Task<string> GenerateUniqueExternalLoginNameAsync(string steamId64, CancellationToken cancellationToken)
    {
        var baseLogin = $"external_{steamId64}";
        var candidate = baseLogin;
        var suffix = 1;
        while (await dbContext.SteamAccounts.AnyAsync(x => x.LoginName == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{baseLogin}_{suffix}";
        }

        return candidate;
    }

    private async Task UpsertGamesForAccountAsync(
        SteamAccount account,
        IReadOnlyCollection<SteamOwnedGame> games,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.SteamAccountGames
            .Where(x => x.AccountId == account.Id)
            .ToListAsync(cancellationToken);
        var existingByAppId = existing.ToDictionary(x => x.AppId);

        foreach (var game in games)
        {
            if (existingByAppId.TryGetValue(game.AppId, out var row))
            {
                row.Name = game.Name;
                row.PlaytimeMinutes = game.PlaytimeMinutes;
                row.ImgIconUrl = game.ImgIconUrl;
                row.LastSyncedAt = syncedAt;
            }
            else
            {
                dbContext.SteamAccountGames.Add(new SteamAccountGame
                {
                    AccountId = account.Id,
                    AppId = game.AppId,
                    Name = game.Name,
                    PlaytimeMinutes = game.PlaytimeMinutes,
                    ImgIconUrl = game.ImgIconUrl,
                    LastSyncedAt = syncedAt
                });
            }
        }

        var incomingIds = games.Select(x => x.AppId).ToHashSet();
        var stale = existing.Where(x => !incomingIds.Contains(x.AppId)).ToArray();
        if (stale.Length > 0)
        {
            dbContext.SteamAccountGames.RemoveRange(stale);
        }

        account.GamesCount = games.Count;
        account.GamesLastSyncAt = syncedAt;
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

