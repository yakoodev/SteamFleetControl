using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Security;

namespace SteamFleet.Persistence.Services;

public sealed partial class JobService(
    SteamFleetDbContext dbContext,
    ISteamAccountGateway steamGateway,
    ISecretCryptoService cryptoService,
    IAuditService auditService) : IJobService
{
    public async Task<JobDto> CreateAsync(JobCreateRequest request, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string>(request.Payload, StringComparer.OrdinalIgnoreCase);
        if (request.Type == JobType.PasswordChange &&
            payload.TryGetValue("newPassword", out var plaintextPassword) &&
            !string.IsNullOrWhiteSpace(plaintextPassword))
        {
            payload.Remove("newPassword");
            payload["encryptedNewPassword"] = cryptoService.Encrypt(plaintextPassword);
            payload["passwordEncryptionVersion"] = cryptoService.Version;
        }

        var payloadJson = JsonSerialization.SerializeDictionary(payload);
        List<FleetJobItem> jobItems;
        int totalCount;

        if (request.Type == JobType.FriendsAddByInvite)
        {
            var pairs = ParseFriendPairs(payload.GetValueOrDefault("pairs"));
            if (pairs.Count == 0)
            {
                throw new InvalidOperationException("For FriendsAddByInvite you must provide at least one source->target pair.");
            }

            var pairAccountIds = pairs
                .SelectMany(x => new[] { x.SourceAccountId, x.TargetAccountId })
                .Distinct()
                .ToArray();

            var activeAccountIds = await dbContext.SteamAccounts
                .Where(x => pairAccountIds.Contains(x.Id) && x.Status != AccountStatus.Archived)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            if (activeAccountIds.Count != pairAccountIds.Length)
            {
                throw new InvalidOperationException("One or more accounts in FriendsAddByInvite pairs are missing or archived.");
            }

            jobItems = pairs.Select(pair =>
            {
                var itemPayload = new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceAccountId"] = pair.SourceAccountId.ToString(),
                    ["targetAccountId"] = pair.TargetAccountId.ToString()
                };

                return new FleetJobItem
                {
                    AccountId = pair.SourceAccountId,
                    RequestJson = JsonSerialization.SerializeDictionary(itemPayload),
                    Status = JobItemStatus.Pending
                };
            }).ToList();

            totalCount = jobItems.Count;
        }
        else if (request.Type == JobType.FriendsConnectFamilyMain)
        {
            if (!payload.TryGetValue("mainAccountId", out var mainAccountIdRaw) ||
                !Guid.TryParse(mainAccountIdRaw, out var mainAccountId))
            {
                throw new InvalidOperationException("mainAccountId is required for FriendsConnectFamilyMain.");
            }

            var mainAccountExists = await dbContext.SteamAccounts
                .AnyAsync(x => x.Id == mainAccountId && x.Status != AccountStatus.Archived, cancellationToken);
            if (!mainAccountExists)
            {
                throw new InvalidOperationException("Main account is missing or archived.");
            }

            var childIds = ParseGuidPipe(payload.GetValueOrDefault("childAccountIds"));
            if (childIds.Count == 0)
            {
                childIds = await dbContext.SteamAccounts
                    .Where(x => x.ParentAccountId == mainAccountId && x.Status != AccountStatus.Archived)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);
            }

            childIds = childIds
                .Where(x => x != mainAccountId)
                .Distinct()
                .ToList();

            if (childIds.Count == 0)
            {
                throw new InvalidOperationException("Family children not found for FriendsConnectFamilyMain.");
            }

            var activeChildIds = await dbContext.SteamAccounts
                .Where(x => childIds.Contains(x.Id) && x.Status != AccountStatus.Archived)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            if (activeChildIds.Count != childIds.Count)
            {
                throw new InvalidOperationException("One or more child accounts are missing or archived.");
            }

            jobItems = childIds.Select(childId =>
            {
                var itemPayload = new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceAccountId"] = mainAccountId.ToString(),
                    ["targetAccountId"] = childId.ToString()
                };

                return new FleetJobItem
                {
                    AccountId = mainAccountId,
                    RequestJson = JsonSerialization.SerializeDictionary(itemPayload),
                    Status = JobItemStatus.Pending
                };
            }).ToList();

            totalCount = jobItems.Count;
        }
        else
        {
            if (request.AccountIds.Count == 0)
            {
                throw new InvalidOperationException("AccountIds must not be empty.");
            }

            var accounts = await dbContext.SteamAccounts
                .Where(x => request.AccountIds.Contains(x.Id) && x.Status != AccountStatus.Archived)
                .ToListAsync(cancellationToken);

            if (accounts.Count == 0)
            {
                throw new InvalidOperationException("No active accounts matched requested ids.");
            }

            jobItems = accounts.Select(account => new FleetJobItem
            {
                AccountId = account.Id,
                RequestJson = payloadJson,
                Status = JobItemStatus.Pending
            }).ToList();
            totalCount = accounts.Count;
        }

        var job = new FleetJob
        {
            Type = request.Type,
            Status = JobStatus.Pending,
            CreatedBy = actorId,
            TotalCount = totalCount,
            DryRun = request.DryRun,
            Parallelism = Math.Clamp(request.Parallelism, 1, 50),
            RetryCount = Math.Clamp(request.RetryCount, 0, 10),
            PayloadJson = payloadJson,
            Items = jobItems
        };

        await dbContext.Jobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.JobCreated,
            "job",
            job.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["type"] = job.Type.ToString(),
                ["dryRun"] = job.DryRun.ToString(),
                ["accountCount"] = job.TotalCount.ToString()
            },
            cancellationToken);

        return MapJob(job);
    }

    public async Task<JobDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.Jobs
            .AsNoTracking()
            .Include(x => x.SensitiveReport)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return job is null ? null : MapJob(job);
    }

    public async Task<IReadOnlyCollection<JobDto>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 500);

        var jobs = await dbContext.Jobs
            .AsNoTracking()
            .Include(x => x.SensitiveReport)
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return jobs.Select(MapJob).ToList();
    }

    public async Task<IReadOnlyCollection<JobItemDto>> GetItemsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var items = await dbContext.JobItems
            .AsNoTracking()
            .Where(x => x.JobId == id)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new JobItemDto
            {
                Id = x.Id,
                JobId = x.JobId,
                AccountId = x.AccountId,
                Status = x.Status,
                Attempt = x.Attempt,
                ErrorText = x.ErrorText,
                StartedAt = x.StartedAt,
                FinishedAt = x.FinishedAt,
                Request = JsonSerialization.DeserializeDictionary(x.RequestJson),
                Result = JsonSerialization.DeserializeDictionary(x.ResultJson)
            })
            .ToListAsync(cancellationToken);

        foreach (var item in items)
        {
            if (item.Result.TryGetValue("reasonCode", out var reasonCode) && !string.IsNullOrWhiteSpace(reasonCode))
            {
                item.ReasonCode = reasonCode;
            }

            if (item.Result.TryGetValue("retryable", out var retryableRaw) &&
                bool.TryParse(retryableRaw, out var retryable))
            {
                item.Retryable = retryable;
            }
        }

        return items;
    }

    public async Task<byte[]?> DownloadSensitiveReportOnceAsync(
        Guid id,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var report = await dbContext.JobSensitiveReports
            .FirstOrDefaultAsync(x => x.JobId == id, cancellationToken);

        if (report is null)
        {
            return null;
        }

        if (report.ConsumedAt is not null)
        {
            throw new InvalidOperationException("Sensitive report has already been consumed.");
        }

        var plaintext = cryptoService.Decrypt(report.EncryptedPayload);
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return null;
        }

        report.ConsumedAt = DateTimeOffset.UtcNow;
        report.ConsumedBy = actorId;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SensitiveReportDownloaded,
            "job",
            id.ToString(),
            actorId,
            ip,
            cancellationToken: cancellationToken);

        return Encoding.UTF8.GetBytes(plaintext);
    }

    public async Task<bool> CancelAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null)
        {
            return false;
        }

        if (job.Status is JobStatus.Completed or JobStatus.Failed)
        {
            return false;
        }

        job.Status = JobStatus.Canceled;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.JobCanceled,
            "job",
            id.ToString(),
            actorId,
            ip,
            cancellationToken: cancellationToken);

        return true;
    }

    public async Task ExecuteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.Jobs
            .Include(x => x.Items)
            .Include(x => x.SensitiveReport)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Job {id} not found");

        if (job.Status is JobStatus.Completed or JobStatus.Running or JobStatus.Canceled)
        {
            return;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.JobStarted,
            "job",
            id.ToString(),
            job.CreatedBy,
            null,
            new Dictionary<string, string> { ["type"] = job.Type.ToString() },
            cancellationToken);

        var payload = JsonSerialization.DeserializeDictionary(job.PayloadJson);
        var sensitiveRows = new List<PasswordReportRow>();

        foreach (var item in job.Items.OrderBy(x => x.CreatedAt))
        {
            if (job.Status == JobStatus.Canceled)
            {
                item.Status = JobItemStatus.Canceled;
                item.FinishedAt = DateTimeOffset.UtcNow;
                continue;
            }

            var account = await dbContext.SteamAccounts
                .Include(x => x.Secret)
                .Include(x => x.TagLinks)
                .ThenInclude(x => x.Tag)
                .FirstOrDefaultAsync(x => x.Id == item.AccountId, cancellationToken);

            if (account is null)
            {
                item.Status = JobItemStatus.Failed;
                item.ErrorText = "Account not found";
                item.ResultJson = JsonSerialization.SerializeDictionary(new Dictionary<string, string>
                {
                    ["reasonCode"] = SteamReasonCodes.SourceAccountMissing,
                    ["retryable"] = false.ToString()
                });
                item.FinishedAt = DateTimeOffset.UtcNow;
                job.FailureCount++;
                continue;
            }

            item.StartedAt = DateTimeOffset.UtcNow;
            item.Status = JobItemStatus.Running;
            await dbContext.SaveChangesAsync(cancellationToken);

            try
            {
                var itemPayload = JsonSerialization.DeserializeDictionary(item.RequestJson);
                if (itemPayload.Count == 0)
                {
                    itemPayload = new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase);
                }

                if (job.DryRun)
                {
                    item.Status = JobItemStatus.Skipped;
                    item.ResultJson = JsonSerialization.SerializeDictionary(new Dictionary<string, string>
                    {
                        ["mode"] = "dry-run",
                        ["jobType"] = job.Type.ToString(),
                        ["reasonCode"] = SteamReasonCodes.None,
                        ["retryable"] = false.ToString()
                    });
                }
                else
                {
                    JobItemProcessingResult processing = JobItemProcessingResult.Fail("Operation was not started.");
                    SteamOperationResult result = processing.Result;

                    var maxAttempts = Math.Max(1, job.RetryCount + 1);
                    for (var attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        item.Attempt = attempt;

                        try
                        {
                            processing = await ProcessItemAsync(
                                job.Type,
                                account,
                                itemPayload,
                                job.CreatedBy ?? "system",
                                cancellationToken);
                            result = processing.Result;
                        }
                        catch (Exception ex)
                        {
                            processing = JobItemProcessingResult.Fail(ex.Message);
                            result = processing.Result;
                        }

                        if (result.Success)
                        {
                            if (!string.IsNullOrWhiteSpace(processing.SensitivePassword))
                            {
                                sensitiveRows.Add(new PasswordReportRow(
                                    account.Id,
                                    account.LoginName,
                                    processing.SensitivePassword,
                                    result.Data.TryGetValue("deauthorized", out var deauthValue) &&
                                    bool.TryParse(deauthValue, out var deauth) &&
                                    deauth));
                            }

                            break;
                        }

                        var canRetry = attempt < maxAttempts && IsRetryable(result);
                        if (!canRetry)
                        {
                            break;
                        }

                        await Task.Delay(GetRetryBackoff(attempt), cancellationToken);
                    }

                    item.Status = result.Success
                        ? JobItemStatus.Success
                        : (result.Retryable ? JobItemStatus.Recoverable : JobItemStatus.Failed);
                    item.ErrorText = result.ErrorMessage;
                    result.Data["reasonCode"] = result.ReasonCode ?? SteamReasonCodes.Unknown;
                    result.Data["retryable"] = result.Retryable.ToString();
                    result.Data["attempt"] = item.Attempt.ToString();
                    item.ResultJson = JsonSerialization.SerializeDictionary(result.Data);

                    if (result.Success)
                    {
                        account.LastSuccessAt = DateTimeOffset.UtcNow;
                        account.LastErrorAt = null;
                        var keepRequiresRelogin =
                            job.Type == JobType.SessionsDeauthorize ||
                            (job.Type == JobType.PasswordChange &&
                             result.Data.TryGetValue("deauthorized", out var deauthValue) &&
                             bool.TryParse(deauthValue, out var deauthorized) &&
                             deauthorized);

                        if (!keepRequiresRelogin &&
                            (account.Status is AccountStatus.Error or AccountStatus.RequiresRelogin))
                        {
                            account.Status = AccountStatus.Active;
                        }
                    }
                    else
                    {
                        account.LastErrorAt = DateTimeOffset.UtcNow;
                        account.Status = AccountStatus.Error;
                        await auditService.WriteAsync(
                            AuditEventType.JobItemFailed,
                            "job_item",
                            item.Id.ToString(),
                            job.CreatedBy,
                            null,
                            new Dictionary<string, string>
                            {
                                ["jobId"] = job.Id.ToString(),
                                ["accountId"] = account.Id.ToString(),
                                ["error"] = result.ErrorMessage ?? "unknown",
                                ["reasonCode"] = result.ReasonCode ?? SteamReasonCodes.Unknown,
                                ["retryable"] = result.Retryable.ToString()
                            },
                            cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                item.Status = JobItemStatus.Failed;
                item.ErrorText = ex.Message;
                item.ResultJson = JsonSerialization.SerializeDictionary(new Dictionary<string, string>
                {
                    ["reasonCode"] = SteamReasonCodes.Unknown,
                    ["retryable"] = false.ToString(),
                    ["attempt"] = item.Attempt.ToString()
                });
                account.LastErrorAt = DateTimeOffset.UtcNow;
                account.Status = AccountStatus.Error;
            }
            finally
            {
                item.FinishedAt = DateTimeOffset.UtcNow;

                if (item.Status is JobItemStatus.Success or JobItemStatus.Skipped)
                {
                    job.SuccessCount++;
                }
                else if (item.Status is JobItemStatus.Failed or JobItemStatus.Recoverable)
                {
                    job.FailureCount++;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        if (!job.DryRun && job.Type == JobType.PasswordChange && sensitiveRows.Count > 0)
        {
            var csv = BuildSensitiveReportCsv(sensitiveRows);
            var encrypted = cryptoService.Encrypt(csv);

            if (job.SensitiveReport is null)
            {
                job.SensitiveReport = new JobSensitiveReport
                {
                    JobId = job.Id,
                    EncryptedPayload = encrypted,
                    EncryptionVersion = cryptoService.Version
                };
                await dbContext.JobSensitiveReports.AddAsync(job.SensitiveReport, cancellationToken);
            }
            else
            {
                job.SensitiveReport.EncryptedPayload = encrypted;
                job.SensitiveReport.EncryptionVersion = cryptoService.Version;
                job.SensitiveReport.ConsumedAt = null;
                job.SensitiveReport.ConsumedBy = null;
            }
        }

        job.Status = job.Status switch
        {
            JobStatus.Canceled => JobStatus.Canceled,
            _ when job.FailureCount > 0 => JobStatus.Failed,
            _ => JobStatus.Completed
        };

        job.FinishedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.JobCompleted,
            "job",
            id.ToString(),
            job.CreatedBy,
            null,
            new Dictionary<string, string>
            {
                ["status"] = job.Status.ToString(),
                ["success"] = job.SuccessCount.ToString(),
                ["failure"] = job.FailureCount.ToString()
            },
            cancellationToken);
    }
}

