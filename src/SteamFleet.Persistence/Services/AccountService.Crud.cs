using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Domain.Entities;

namespace SteamFleet.Persistence.Services;

public sealed partial class AccountService
{
    public async Task<AccountsPageResult> GetAsync(AccountFilterRequest request, CancellationToken cancellationToken = default)
    {
        var query = dbContext.SteamAccounts
            .AsNoTracking()
            .Include(x => x.Folder)
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.LoginName.ToLower().Contains(q) ||
                (x.DisplayName != null && x.DisplayName.ToLower().Contains(q)) ||
                (x.Email != null && x.Email.ToLower().Contains(q)) ||
                (x.SteamId64 != null && x.SteamId64.Contains(q)));
        }

        if (request.Status is not null)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        if (request.FolderId is not null)
        {
            query = query.Where(x => x.FolderId == request.FolderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            var tag = request.Tag.Trim().ToLowerInvariant();
            query = query.Where(x => x.TagLinks.Any(t => t.Tag != null && t.Tag.Name.ToLower() == tag));
        }

        if (!string.IsNullOrWhiteSpace(request.FamilyGroup))
        {
            var familyGroup = request.FamilyGroup.Trim();
            if (familyGroup.Equals("ungrouped", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => string.IsNullOrWhiteSpace(x.SteamFamilyId));
            }
            else if (familyGroup.Equals("family", StringComparison.OrdinalIgnoreCase) ||
                     familyGroup.Equals("with", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => !string.IsNullOrWhiteSpace(x.SteamFamilyId));
            }
            else
            {
                query = query.Where(x => x.SteamFamilyId == familyGroup);
            }
        }

        var total = await query.CountAsync(cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var skip = (page - 1) * pageSize;

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var familyCounts = await BuildFamilyCountsAsync(items.Select(x => x.Id).ToArray(), cancellationToken);

        return new AccountsPageResult
        {
            TotalCount = total,
            Items = items.Select(x => MapAccountDto(x, familyCounts.GetValueOrDefault(x.SteamFamilyId ?? x.Id.ToString(), 1))).ToArray()
        };
    }

    public async Task<AccountDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SteamAccounts
            .AsNoTracking()
            .Include(x => x.Folder)
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var familyCounts = await BuildFamilyCountsAsync([entity.Id], cancellationToken);
        return MapAccountDto(entity, familyCounts.GetValueOrDefault(entity.SteamFamilyId ?? entity.Id.ToString(), 1));
    }

    public async Task<AccountDto> CreateAsync(AccountUpsertRequest request, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.SteamAccounts
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.LoginName == request.LoginName, cancellationToken);

        if (existing is not null)
        {
            throw new InvalidOperationException($"Account with login '{request.LoginName}' already exists.");
        }

        var entity = new SteamAccount
        {
            LoginName = request.LoginName,
            CreatedBy = actorId,
            UpdatedBy = actorId
        };

        await ApplyUpsertAsync(entity, request, actorId, cancellationToken);
        await dbContext.SteamAccounts.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountCreated,
            "steam_account",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["login"] = entity.LoginName,
                ["status"] = entity.Status.ToString()
            },
            cancellationToken);

        return await GetByIdAsync(entity.Id, cancellationToken)
               ?? throw new InvalidOperationException("Failed to load created account.");
    }

    public async Task<AccountDto?> UpdateAsync(Guid id, AccountUpsertRequest request, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SteamAccounts
            .Include(x => x.TagLinks)
            .ThenInclude(x => x.Tag)
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        await ApplyUpsertAsync(entity, request, actorId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["login"] = entity.LoginName },
            cancellationToken);

        return await GetByIdAsync(entity.Id, cancellationToken);
    }

    public async Task<bool> ArchiveAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SteamAccounts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.Status = AccountStatus.Archived;
        entity.UpdatedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.AccountArchived,
            "steam_account",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["login"] = entity.LoginName },
            cancellationToken);

        return true;
    }

    public async Task<AccountImportResult> ImportAsync(Stream stream, string fileName, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var requests = await ParseImportAsync(stream, fileName, cancellationToken);
        var result = new AccountImportResult { Total = requests.Count };

        foreach (var request in requests)
        {
            try
            {
                var existing = await dbContext.SteamAccounts
                    .Include(x => x.TagLinks)
                    .ThenInclude(x => x.Tag)
                    .Include(x => x.Secret)
                    .FirstOrDefaultAsync(x => x.LoginName == request.LoginName, cancellationToken);

                if (existing is null)
                {
                    var newAccount = new SteamAccount
                    {
                        LoginName = request.LoginName,
                        CreatedBy = actorId,
                        UpdatedBy = actorId
                    };

                    await ApplyUpsertAsync(newAccount, request, actorId, cancellationToken);
                    await dbContext.SteamAccounts.AddAsync(newAccount, cancellationToken);
                    result.Created++;
                }
                else
                {
                    await ApplyUpsertAsync(existing, request, actorId, cancellationToken);
                    result.Updated++;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{request.LoginName}: {ex.Message}");
            }
        }

        await auditService.WriteAsync(
            AuditEventType.AccountUpdated,
            "steam_account",
            "import",
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["total"] = result.Total.ToString(),
                ["created"] = result.Created.ToString(),
                ["updated"] = result.Updated.ToString(),
                ["errors"] = result.Errors.Count.ToString()
            },
            cancellationToken);

        return result;
    }

    public async Task<byte[]> ExportCsvAsync(AccountFilterRequest filter, CancellationToken cancellationToken = default)
    {
        var page = await GetAsync(new AccountFilterRequest
        {
            Query = filter.Query,
            Status = filter.Status,
            FolderId = filter.FolderId,
            Tag = filter.Tag,
            FamilyGroup = filter.FamilyGroup,
            Page = 1,
            PageSize = 5000
        }, cancellationToken);

        await using var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteField("id");
        csv.WriteField("login");
        csv.WriteField("displayName");
        csv.WriteField("steamId64");
        csv.WriteField("profileUrl");
        csv.WriteField("email");
        csv.WriteField("folder");
        csv.WriteField("familyId");
        csv.WriteField("familyRole");
        csv.WriteField("isExternal");
        csv.WriteField("gamesCount");
        csv.WriteField("status");
        csv.WriteField("tags");
        csv.WriteField("lastCheckAt");
        csv.NextRecord();

        foreach (var item in page.Items)
        {
            csv.WriteField(item.Id);
            csv.WriteField(item.LoginName);
            csv.WriteField(item.DisplayName);
            csv.WriteField(item.SteamId64);
            csv.WriteField(item.ProfileUrl);
            csv.WriteField(item.Email);
            csv.WriteField(item.FolderName);
            csv.WriteField(item.SteamFamilyId);
            csv.WriteField(item.SteamFamilyRole);
            csv.WriteField(item.IsExternal);
            csv.WriteField(item.GamesCount);
            csv.WriteField(item.Status);
            csv.WriteField(string.Join('|', item.Tags));
            csv.WriteField(item.LastCheckAt?.ToString("O"));
            csv.NextRecord();
        }

        await writer.FlushAsync(cancellationToken);
        return ms.ToArray();
    }
}
