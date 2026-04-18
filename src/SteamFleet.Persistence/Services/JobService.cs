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

public sealed class JobService(
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

    private async Task<JobItemProcessingResult> ProcessItemAsync(
        JobType type,
        SteamAccount account,
        Dictionary<string, string> payload,
        string actorId,
        CancellationToken cancellationToken)
    {
        return type switch
        {
            JobType.SessionValidate => JobItemProcessingResult.FromResult(await HandleValidateSessionAsync(account, cancellationToken)),
            JobType.SessionRefresh => JobItemProcessingResult.FromResult(await HandleRefreshSessionAsync(account, cancellationToken)),
            JobType.ProfileUpdate => JobItemProcessingResult.FromResult(await HandleProfileUpdateAsync(account, payload, cancellationToken)),
            JobType.PrivacyUpdate => JobItemProcessingResult.FromResult(await HandlePrivacyUpdateAsync(account, payload, cancellationToken)),
            JobType.AvatarUpdate => JobItemProcessingResult.FromResult(await HandleAvatarUpdateAsync(account, payload, cancellationToken)),
            JobType.TagsAssign => JobItemProcessingResult.FromResult(await HandleTagsAssignAsync(account, payload, cancellationToken)),
            JobType.GroupMove => JobItemProcessingResult.FromResult(await HandleGroupMoveAsync(account, payload, cancellationToken)),
            JobType.AddNote => JobItemProcessingResult.FromResult(await HandleAddNoteAsync(account, payload, cancellationToken)),
            JobType.PasswordChange => await HandlePasswordChangeAsync(account, payload, actorId, cancellationToken),
            JobType.SessionsDeauthorize => JobItemProcessingResult.FromResult(await HandleSessionsDeauthorizeAsync(account, actorId, cancellationToken)),
            JobType.FriendsAddByInvite => JobItemProcessingResult.FromResult(await HandleFriendsAddByInviteAsync(account, payload, actorId, cancellationToken)),
            JobType.FriendsConnectFamilyMain => JobItemProcessingResult.FromResult(await HandleFriendsAddByInviteAsync(account, payload, actorId, cancellationToken)),
            _ => JobItemProcessingResult.Fail($"Unsupported job type {type}", SteamReasonCodes.EndpointRejected)
        };
    }

    private async Task<SteamOperationResult> HandleValidateSessionAsync(SteamAccount account, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        var validation = await steamGateway.ValidateSessionAsync(ensuredSession.SessionPayload, cancellationToken);
        account.LastCheckAt = DateTimeOffset.UtcNow;
        return new SteamOperationResult
        {
            Success = validation.IsValid,
            ErrorMessage = validation.IsValid ? null : validation.Reason,
            Data = new Dictionary<string, string>
            {
                ["isValid"] = validation.IsValid.ToString(),
                ["reason"] = validation.Reason ?? string.Empty
            }
        };
    }

    private async Task<SteamOperationResult> HandleRefreshSessionAsync(SteamAccount account, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        SteamSessionInfo refreshed;
        try
        {
            refreshed = await steamGateway.RefreshSessionAsync(ensuredSession.SessionPayload, cancellationToken);
        }
        catch (Exception ex)
        {
            var reauth = await EnsureSessionPayloadAsync(account, cancellationToken, forceReauth: true);
            if (!reauth.Success || string.IsNullOrWhiteSpace(reauth.SessionPayload))
            {
                return new SteamOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Refresh failed: {ex.Message}. Re-auth failed: {reauth.ErrorMessage}"
                };
            }

            return new SteamOperationResult
            {
                Success = true,
                Data = new Dictionary<string, string>
                {
                    ["mode"] = "reauth",
                    ["expiresAt"] = account.LastSuccessAt?.ToString("O") ?? string.Empty
                }
            };
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(refreshed.CookiePayload ?? ensuredSession.SessionPayload);
        account.Secret.EncryptionVersion = cryptoService.Version;

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string>
            {
                ["expiresAt"] = refreshed.ExpiresAt?.ToString("O") ?? string.Empty
            }
        };
    }

    private async Task<SteamOperationResult> HandleProfileUpdateAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        var profile = new SteamProfileData
        {
            DisplayName = payload.GetValueOrDefault("displayName"),
            Summary = payload.GetValueOrDefault("summary"),
            RealName = payload.GetValueOrDefault("realName"),
            Country = payload.GetValueOrDefault("country"),
            State = payload.GetValueOrDefault("state"),
            City = payload.GetValueOrDefault("city"),
            CustomUrl = payload.GetValueOrDefault("customUrl")
        };

        var result = await steamGateway.UpdateProfileAsync(ensuredSession.SessionPayload, profile, cancellationToken);
        if (result.Success)
        {
            account.DisplayName = profile.DisplayName ?? account.DisplayName;
        }

        return result;
    }

    private async Task<SteamOperationResult> HandlePrivacyUpdateAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        var settings = new SteamPrivacySettings
        {
            ProfilePrivate = bool.TryParse(payload.GetValueOrDefault("profilePrivate"), out var profilePrivate) && profilePrivate,
            FriendsPrivate = bool.TryParse(payload.GetValueOrDefault("friendsPrivate"), out var friendsPrivate) && friendsPrivate,
            InventoryPrivate = bool.TryParse(payload.GetValueOrDefault("inventoryPrivate"), out var inventoryPrivate) && inventoryPrivate
        };

        return await steamGateway.UpdatePrivacySettingsAsync(ensuredSession.SessionPayload, settings, cancellationToken);
    }

    private async Task<SteamOperationResult> HandleAvatarUpdateAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        if (!payload.TryGetValue("avatarBase64", out var avatarBase64) || string.IsNullOrWhiteSpace(avatarBase64))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "avatarBase64 payload is required" };
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(avatarBase64);
        }
        catch
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "avatarBase64 is invalid base64" };
        }

        return await steamGateway.UpdateAvatarAsync(ensuredSession.SessionPayload, bytes, "avatar.png", cancellationToken);
    }

    private async Task<SteamOperationResult> HandleTagsAssignAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("tags", out var tagsRaw) || string.IsNullOrWhiteSpace(tagsRaw))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "tags payload is required" };
        }

        var tags = tagsRaw.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tags.Length == 0)
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "No tags supplied" };
        }

        account.TagLinks.Clear();
        var existingTags = await dbContext.SteamAccountTags.Where(t => tags.Contains(t.Name)).ToListAsync(cancellationToken);
        var missing = tags.Except(existingTags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in missing)
        {
            var tag = new SteamAccountTag { Name = tagName };
            existingTags.Add(tag);
            await dbContext.SteamAccountTags.AddAsync(tag, cancellationToken);
        }

        foreach (var tag in existingTags)
        {
            account.TagLinks.Add(new SteamAccountTagLink
            {
                AccountId = account.Id,
                TagId = tag.Id,
                Account = account,
                Tag = tag
            });
        }

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string> { ["tags"] = string.Join('|', tags) }
        };
    }

    private async Task<SteamOperationResult> HandleGroupMoveAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("folder", out var folderName) || string.IsNullOrWhiteSpace(folderName))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "folder payload is required" };
        }

        var folder = await dbContext.Folders.FirstOrDefaultAsync(x => x.Name == folderName, cancellationToken);
        if (folder is null)
        {
            folder = new Folder { Name = folderName };
            await dbContext.Folders.AddAsync(folder, cancellationToken);
        }

        account.Folder = folder;
        account.FolderId = folder.Id;

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string> { ["folder"] = folderName }
        };
    }

    private Task<SteamOperationResult> HandleAddNoteAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        payload.TryGetValue("note", out var note);

        if (string.IsNullOrWhiteSpace(note))
        {
            return Task.FromResult(new SteamOperationResult { Success = false, ErrorMessage = "note payload is required" });
        }

        account.Note = string.IsNullOrWhiteSpace(account.Note)
            ? note
            : $"{account.Note}{Environment.NewLine}{DateTimeOffset.UtcNow:O}: {note}";

        return Task.FromResult(new SteamOperationResult { Success = true });
    }

    private async Task<JobItemProcessingResult> HandlePasswordChangeAsync(
        SteamAccount account,
        Dictionary<string, string> payload,
        string actorId,
        CancellationToken cancellationToken)
    {
        var currentPassword = payload.TryGetValue("currentPassword", out var payloadCurrentPassword) &&
                              !string.IsNullOrWhiteSpace(payloadCurrentPassword)
            ? payloadCurrentPassword
            : await DecryptSecretAsync(account, account.Secret?.EncryptedPassword, "password", cancellationToken);

        if (string.IsNullOrWhiteSpace(currentPassword))
        {
            return JobItemProcessingResult.Fail(
                "Current password is not configured.",
                SteamReasonCodes.AuthSessionMissing);
        }

        var fixedPassword = payload.GetValueOrDefault("newPassword");
        if (string.IsNullOrWhiteSpace(fixedPassword) &&
            payload.TryGetValue("encryptedNewPassword", out var encryptedFixedPassword) &&
            !string.IsNullOrWhiteSpace(encryptedFixedPassword))
        {
            fixedPassword = cryptoService.Decrypt(encryptedFixedPassword);
        }
        var generated = string.IsNullOrWhiteSpace(fixedPassword);
        var passwordLength = int.TryParse(payload.GetValueOrDefault("generateLength"), out var parsedLength)
            ? Math.Clamp(parsedLength, 12, 64)
            : 20;
        var nextPassword = generated ? GenerateStrongPassword(passwordLength) : fixedPassword!;

        var deauthorizeAfterChange =
            bool.TryParse(payload.GetValueOrDefault("deauthorizeAfterChange"), out var parsedDeauthorize) &&
            parsedDeauthorize;

        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return JobItemProcessingResult.Fail(
                ensuredSession.ErrorMessage ?? "Session is unavailable.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        var change = await steamGateway.ChangePasswordAsync(
            ensuredSession.SessionPayload,
            currentPassword,
            nextPassword,
            cancellationToken: cancellationToken);

        if (!change.Success)
        {
            return JobItemProcessingResult.Fail(
                change.ErrorMessage ?? "Password change failed.",
                change.ReasonCode,
                change.Retryable);
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedPassword = cryptoService.Encrypt(nextPassword);
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.Status = AccountStatus.Active;

        var deauthorized = false;
        string? deauthorizeWarning = null;
        string? deauthorizeReasonCode = null;
        var deauthorizeRetryable = false;
        if (deauthorizeAfterChange)
        {
            var deauthResult = await steamGateway.DeauthorizeAllSessionsAsync(ensuredSession.SessionPayload, cancellationToken);
            if (deauthResult.Success)
            {
                account.Secret.EncryptedSessionPayload = null;
                account.Status = AccountStatus.RequiresRelogin;
                deauthorized = true;
            }
            else
            {
                deauthorizeWarning = string.IsNullOrWhiteSpace(deauthResult.ErrorMessage)
                    ? "Password changed but Steam did not confirm deauthorization."
                    : $"Password changed but deauthorize failed: {deauthResult.ErrorMessage}";
                deauthorizeReasonCode = deauthResult.ReasonCode;
                deauthorizeRetryable = deauthResult.Retryable;
                account.LastErrorAt = DateTimeOffset.UtcNow;
                account.Status = AccountStatus.Active;
            }
        }

        var result = new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string>
            {
                ["passwordChanged"] = true.ToString(),
                ["generated"] = generated.ToString(),
                ["deauthorized"] = deauthorized.ToString()
            }
        };
        if (!string.IsNullOrWhiteSpace(deauthorizeWarning))
        {
            result.Data["deauthorizeWarning"] = deauthorizeWarning;
            if (!string.IsNullOrWhiteSpace(deauthorizeReasonCode))
            {
                result.Data["deauthorizeReasonCode"] = deauthorizeReasonCode;
            }

            result.Data["deauthorizeRetryable"] = deauthorizeRetryable.ToString();
        }

        await auditService.WriteAsync(
            AuditEventType.PasswordChanged,
            "steam_account",
            account.Id.ToString(),
            actorId,
            null,
            new Dictionary<string, string>
            {
                ["deauthorized"] = deauthorized.ToString(),
                ["generated"] = generated.ToString(),
                ["deauthorizeWarning"] = (!string.IsNullOrWhiteSpace(deauthorizeWarning)).ToString(),
                ["source"] = "job"
            },
            cancellationToken);

        return new JobItemProcessingResult
        {
            Result = result,
            SensitivePassword = nextPassword
        };
    }

    private async Task<SteamOperationResult> HandleSessionsDeauthorizeAsync(
        SteamAccount account,
        string actorId,
        CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ensuredSession.ErrorMessage,
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        var deauth = await steamGateway.DeauthorizeAllSessionsAsync(ensuredSession.SessionPayload, cancellationToken);
        if (!deauth.Success)
        {
            return deauth;
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedSessionPayload = null;
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.Status = AccountStatus.RequiresRelogin;

        await auditService.WriteAsync(
            AuditEventType.SessionsDeauthorized,
            "steam_account",
            account.Id.ToString(),
            actorId,
            null,
            new Dictionary<string, string> { ["source"] = "job" },
            cancellationToken);

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string> { ["deauthorized"] = true.ToString() }
        };
    }

    private async Task<SteamOperationResult> HandleFriendsAddByInviteAsync(
        SteamAccount sourceAccount,
        Dictionary<string, string> payload,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("targetAccountId", out var targetAccountIdRaw) ||
            !Guid.TryParse(targetAccountIdRaw, out var targetAccountId))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "targetAccountId is missing for friends connect item.",
                ReasonCode = SteamReasonCodes.TargetAccountMissing
            };
        }

        var targetAccount = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == targetAccountId, cancellationToken);
        if (targetAccount is null)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Target account not found.",
                ReasonCode = SteamReasonCodes.TargetAccountMissing
            };
        }

        var sourceSession = await EnsureSessionPayloadAsync(sourceAccount, cancellationToken);
        if (!sourceSession.Success || string.IsNullOrWhiteSpace(sourceSession.SessionPayload))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = sourceSession.ErrorMessage,
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        var targetSession = await EnsureSessionPayloadAsync(targetAccount, cancellationToken);
        if (!targetSession.Success || string.IsNullOrWhiteSpace(targetSession.SessionPayload))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = targetSession.ErrorMessage,
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        SteamFriendInviteLink invite;
        try
        {
            invite = await steamGateway.GetFriendInviteLinkAsync(sourceSession.SessionPayload, cancellationToken);
        }
        catch (SteamGatewayOperationException ex)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = ex.ReasonCode ?? SteamReasonCodes.Unknown,
                Retryable = ex.Retryable
            };
        }
        catch (Exception ex)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = SteamReasonCodes.Unknown,
                Retryable = true
            };
        }

        if (string.IsNullOrWhiteSpace(invite.InviteUrl))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Invite URL was not produced by source account.",
                ReasonCode = SteamReasonCodes.EndpointRejected
            };
        }

        SteamOperationResult accept;
        try
        {
            accept = await steamGateway.AcceptFriendInviteAsync(targetSession.SessionPayload, invite.InviteUrl, cancellationToken);
        }
        catch (SteamGatewayOperationException ex)
        {
            accept = new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = ex.ReasonCode ?? SteamReasonCodes.Unknown,
                Retryable = ex.Retryable
            };
        }
        catch (Exception ex)
        {
            accept = new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = SteamReasonCodes.Unknown,
                Retryable = true
            };
        }

        if (!accept.Success)
        {
            targetAccount.LastErrorAt = DateTimeOffset.UtcNow;
            targetAccount.Status = accept.ReasonCode is SteamReasonCodes.AuthSessionMissing or SteamReasonCodes.GuardPending
                ? AccountStatus.RequiresRelogin
                : AccountStatus.Error;

            await auditService.WriteAsync(
                AuditEventType.FriendConnectFailed,
                "steam_account",
                targetAccount.Id.ToString(),
                actorId,
                null,
                new Dictionary<string, string>
                {
                    ["sourceAccountId"] = sourceAccount.Id.ToString(),
                    ["targetAccountId"] = targetAccount.Id.ToString(),
                    ["reasonCode"] = accept.ReasonCode ?? SteamReasonCodes.Unknown,
                    ["retryable"] = accept.Retryable.ToString()
                },
                cancellationToken);
            return accept;
        }

        sourceAccount.LastSuccessAt = DateTimeOffset.UtcNow;
        sourceAccount.LastErrorAt = null;
        if (sourceAccount.Status is AccountStatus.Error or AccountStatus.RequiresRelogin)
        {
            sourceAccount.Status = AccountStatus.Active;
        }

        targetAccount.LastSuccessAt = DateTimeOffset.UtcNow;
        targetAccount.LastErrorAt = null;
        if (targetAccount.Status is AccountStatus.Error or AccountStatus.RequiresRelogin)
        {
            targetAccount.Status = AccountStatus.Active;
        }

        await auditService.WriteAsync(
            AuditEventType.FriendInviteAccepted,
            "steam_account",
            targetAccount.Id.ToString(),
            actorId,
            null,
            new Dictionary<string, string>
            {
                ["sourceAccountId"] = sourceAccount.Id.ToString(),
                ["targetAccountId"] = targetAccount.Id.ToString()
            },
            cancellationToken);

        return new SteamOperationResult
        {
            Success = true,
            ReasonCode = SteamReasonCodes.None,
            Data = new Dictionary<string, string>
            {
                ["sourceAccountId"] = sourceAccount.Id.ToString(),
                ["targetAccountId"] = targetAccount.Id.ToString(),
                ["inviteCode"] = invite.InviteCode
            }
        };
    }

    private async Task<SessionResolutionResult> EnsureSessionPayloadAsync(
        SteamAccount account,
        CancellationToken cancellationToken,
        bool forceReauth = false)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        var existingSession = await DecryptSecretAsync(account, account.Secret.EncryptedSessionPayload, "session_payload", cancellationToken);
        if (!forceReauth && !string.IsNullOrWhiteSpace(existingSession))
        {
            if (TryNormalizeSessionPayload(existingSession, account.LoginName, account.SteamId64, out var normalizedSession))
            {
                existingSession = normalizedSession;
                account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(normalizedSession);
                account.Secret.EncryptionVersion = cryptoService.Version;
            }

            var validation = await steamGateway.ValidateSessionAsync(existingSession, cancellationToken);
            account.LastCheckAt = DateTimeOffset.UtcNow;
            if (validation.IsValid)
            {
                return SessionResolutionResult.FromPayload(existingSession);
            }

            try
            {
                var refreshed = await steamGateway.RefreshSessionAsync(existingSession, cancellationToken);
                if (!string.IsNullOrWhiteSpace(refreshed.CookiePayload))
                {
                    account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(refreshed.CookiePayload);
                    account.Secret.EncryptionVersion = cryptoService.Version;
                    account.Status = AccountStatus.Active;
                    account.LastSuccessAt = DateTimeOffset.UtcNow;
                    account.LastErrorAt = null;
                    return SessionResolutionResult.FromPayload(refreshed.CookiePayload);
                }
            }
            catch
            {
                // ignored, fallback to full re-auth.
            }
        }

        var password = await DecryptSecretAsync(account, account.Secret.EncryptedPassword, "password", cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            return SessionResolutionResult.Fail(
                "Session missing and password not configured.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        var sharedSecret = await DecryptSecretAsync(account, account.Secret.EncryptedSharedSecret, "shared_secret", cancellationToken);
        var identitySecret = await DecryptSecretAsync(account, account.Secret.EncryptedIdentitySecret, "identity_secret", cancellationToken);
        var guardData = await DecryptSecretAsync(account, account.Secret.EncryptedRecoveryPayload, "guard_data", cancellationToken);

        var auth = await steamGateway.AuthenticateAsync(new SteamCredentials
        {
            LoginName = account.LoginName,
            Password = password,
            SharedSecret = sharedSecret,
            IdentitySecret = identitySecret,
            GuardData = guardData,
            AllowDeviceConfirmation = true
        }, cancellationToken);

        if (!auth.Success || string.IsNullOrWhiteSpace(auth.Session.CookiePayload))
        {
            account.LastErrorAt = DateTimeOffset.UtcNow;
            account.Status = AccountStatus.RequiresRelogin;
            return SessionResolutionResult.Fail(
                auth.ErrorMessage ?? "Steam authentication failed.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(auth.Session.CookiePayload);
        if (!string.IsNullOrWhiteSpace(auth.GuardData))
        {
            account.Secret.EncryptedRecoveryPayload = cryptoService.Encrypt(auth.GuardData);
        }

        account.Secret.EncryptionVersion = cryptoService.Version;
        account.Status = AccountStatus.Active;
        account.LastCheckAt = DateTimeOffset.UtcNow;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.SteamId64 = string.IsNullOrWhiteSpace(auth.SteamId64) ? account.SteamId64 : auth.SteamId64;

        return SessionResolutionResult.FromPayload(auth.Session.CookiePayload);
    }

    private async Task<string?> DecryptSecretAsync(
        SteamAccount account,
        string? encryptedValue,
        string fieldName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
        {
            return null;
        }

        await auditService.WriteAsync(
            AuditEventType.SecretRead,
            "steam_account_secret",
            account.Id.ToString(),
            "job-worker",
            null,
            new Dictionary<string, string> { ["field"] = fieldName },
            cancellationToken);

        return cryptoService.Decrypt(encryptedValue);
    }

    private static bool TryNormalizeSessionPayload(string payload, string loginName, string? steamId64, out string normalized)
    {
        normalized = payload;
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(loginName))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root)
            {
                return false;
            }

            var changed = false;
            if (string.IsNullOrWhiteSpace(root["AccountName"]?.GetValue<string?>()))
            {
                root["AccountName"] = loginName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(root["LoginName"]?.GetValue<string?>()))
            {
                root["LoginName"] = loginName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(steamId64) &&
                string.IsNullOrWhiteSpace(root["SteamId64"]?.GetValue<string?>()))
            {
                root["SteamId64"] = steamId64;
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            normalized = root.ToJsonString(JsonSerialization.Defaults);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string BuildSensitiveReportCsv(IReadOnlyCollection<PasswordReportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("accountId,login,newPassword,deauthorized");
        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(row.AccountId.ToString()));
            sb.Append(',');
            sb.Append(EscapeCsv(row.LoginName));
            sb.Append(',');
            sb.Append(EscapeCsv(row.NewPassword));
            sb.Append(',');
            sb.Append(EscapeCsv(row.Deauthorized.ToString()));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"'))
        {
            value = value.Replace("\"", "\"\"");
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? $"\"{value}\""
            : value;
    }

    private static string GenerateStrongPassword(int length)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        // Keep symbol set aligned with single-account password generator.
        const string special = "!@#$*_-";
        var all = upper + lower + digits + special;

        var chars = new List<char>(length)
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            special[RandomNumberGenerator.GetInt32(special.Length)]
        };

        while (chars.Count < length)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars.ToArray());
    }

    private static List<FriendPair> ParseFriendPairs(string? raw)
    {
        var pairs = new List<FriendPair>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return pairs;
        }

        var tokens = raw
            .Split([';', '\r', '\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var normalized = token.Replace("=>", ":", StringComparison.Ordinal)
                .Replace("->", ":", StringComparison.Ordinal)
                .Replace(">", ":", StringComparison.Ordinal);

            var parts = normalized.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !Guid.TryParse(parts[0], out var sourceAccountId) ||
                !Guid.TryParse(parts[1], out var targetAccountId) ||
                sourceAccountId == targetAccountId)
            {
                continue;
            }

            pairs.Add(new FriendPair(sourceAccountId, targetAccountId));
        }

        return pairs
            .Distinct()
            .ToList();
    }

    private static List<Guid> ParseGuidPipe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(['|', ',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Guid.TryParse(x, out var parsed) ? parsed : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
    }

    private static bool IsRetryable(SteamOperationResult result)
    {
        if (result.Retryable)
        {
            return true;
        }

        if (string.Equals(result.ReasonCode, SteamReasonCodes.Timeout, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.AuthSessionMissing, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.ReasonCode, SteamReasonCodes.AntiBotBlocked, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsRecoverableError(result.ErrorMessage);
    }

    private static bool IsRecoverableError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        var text = errorMessage.ToLowerInvariant();
        return text.Contains("timeout", StringComparison.Ordinal) ||
               text.Contains("tempor", StringComparison.Ordinal) ||
               text.Contains("rate limit", StringComparison.Ordinal) ||
               text.Contains("too many", StringComparison.Ordinal) ||
               text.Contains("session", StringComparison.Ordinal) ||
               text.Contains("unauthorized", StringComparison.Ordinal) ||
               text.Contains("expired", StringComparison.Ordinal) ||
               text.Contains("guard", StringComparison.Ordinal) ||
               text.Contains("captcha", StringComparison.Ordinal) ||
               text.Contains("service unavailable", StringComparison.Ordinal);
    }

    private static TimeSpan GetRetryBackoff(int attempt)
    {
        var seconds = Math.Min(60, (int)Math.Pow(2, Math.Max(1, attempt)));
        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class SessionResolutionResult
    {
        public bool Success { get; private init; }
        public string? SessionPayload { get; private init; }
        public string? ErrorMessage { get; private init; }
        public string? ReasonCode { get; private init; }
        public bool Retryable { get; private init; }

        public static SessionResolutionResult FromPayload(string payload) => new()
        {
            Success = true,
            SessionPayload = payload,
            ReasonCode = SteamReasonCodes.None
        };

        public static SessionResolutionResult Fail(string message, string? reasonCode = null, bool retryable = true) => new()
        {
            Success = false,
            ErrorMessage = message,
            ReasonCode = reasonCode ?? SteamReasonCodes.AuthSessionMissing,
            Retryable = retryable
        };
    }

    private sealed class JobItemProcessingResult
    {
        public SteamOperationResult Result { get; init; } = new();
        public string? SensitivePassword { get; init; }

        public static JobItemProcessingResult FromResult(SteamOperationResult result) => new() { Result = result };

        public static JobItemProcessingResult Fail(string message, string? reasonCode = null, bool retryable = false) => new()
        {
            Result = new SteamOperationResult
            {
                Success = false,
                ErrorMessage = message,
                ReasonCode = reasonCode ?? SteamReasonCodes.Unknown,
                Retryable = retryable
            }
        };
    }

    private sealed record FriendPair(Guid SourceAccountId, Guid TargetAccountId);
    private sealed record PasswordReportRow(Guid AccountId, string LoginName, string NewPassword, bool Deauthorized);

    private static JobDto MapJob(FleetJob job)
    {
        return new JobDto
        {
            Id = job.Id,
            Type = job.Type,
            Status = job.Status,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            FinishedAt = job.FinishedAt,
            CreatedBy = job.CreatedBy,
            TotalCount = job.TotalCount,
            SuccessCount = job.SuccessCount,
            FailureCount = job.FailureCount,
            DryRun = job.DryRun,
            HasSensitiveReport = job.SensitiveReport is not null,
            SensitiveReportConsumed = job.SensitiveReport?.ConsumedAt is not null,
            Payload = JsonSerialization.DeserializeDictionary(job.PayloadJson)
        };
    }
}
