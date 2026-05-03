using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Persistence;
using SteamFleet.Persistence.Services;

namespace SteamFleet.Web.Controllers.Api;

[ApiController]
[Route("internal/v2/worker")]
[IgnoreAntiforgeryToken]
public sealed class WorkerIntegrationAdapterController(
    SteamFleetDbContext dbContext,
    IConfiguration configuration,
    IAccountService accountService,
    IJobService jobService,
    IBackgroundJobClient backgroundJobs,
    ILogger<WorkerIntegrationAdapterController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions WorkerJsonOptions = CreateWorkerJsonOptions();

    [HttpGet("health")]
    public IActionResult Health()
    {
        if (!TryRequireWorkerServiceToken(out var unauthorizedResult))
        {
            return unauthorizedResult!;
        }

        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            status = "ok",
        });
    }

    [HttpGet("capabilities")]
    public IActionResult Capabilities()
    {
        if (!TryRequireWorkerServiceToken(out var unauthorizedResult))
        {
            return unauthorizedResult!;
        }

        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            provider = "steam",
            features = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["ext.integration.steam.read"] = true,
                ["ext.integration.steam.jobs"] = true,
                ["ext.account.proxy-credentials.apply"] = true,
            },
            capabilities = new[]
            {
                new { key = "ext.integration.steam.read", enabled = true },
                new { key = "ext.integration.steam.jobs", enabled = true },
                new { key = "ext.account.proxy-credentials.apply", enabled = true },
            },
        });
    }

    [HttpGet("account")]
    public IActionResult Account()
    {
        if (!TryRequireWorkerServiceToken(out var unauthorizedResult))
        {
            return unauthorizedResult!;
        }

        var runtimeContext = ResolveRuntimeContext();
        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            account = new
            {
                provider = "steam",
                accountId = runtimeContext.RuntimeAccountId,
                nickname = $"steam-runtime-{runtimeContext.RuntimeAccountIdShort}",
                status = "active",
                profile = new
                {
                    projectId = runtimeContext.ProjectId,
                    storageNamespace = runtimeContext.StorageNamespace,
                },
                raw = new
                {
                    runtimeAccountId = runtimeContext.RuntimeAccountId,
                    projectId = runtimeContext.ProjectId,
                    storageNamespace = runtimeContext.StorageNamespace,
                },
            },
        });
    }

    [HttpPost("actions/{actionKey}")]
    public async Task<IActionResult> InvokeActionAsync(string actionKey, [FromBody] WorkerActionRequest? request, CancellationToken cancellationToken)
    {
        if (!TryRequireWorkerServiceToken(out var unauthorizedResult))
        {
            return unauthorizedResult!;
        }

        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return BadRequest(new { error = "action is required" });
        }

        var runtimeContext = ResolveRuntimeContext();
        var normalizedAction = actionKey.Trim().ToLowerInvariant();
        var payload = request?.Payload is { ValueKind: JsonValueKind.Object } payloadElement
            ? payloadElement
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

        try
        {
            return normalizedAction switch
            {
                "ext.integration.steam.read" => Ok(new
                {
                    requestId = HttpContext.TraceIdentifier,
                    result = await HandleReadOperationAsync(payload, runtimeContext, cancellationToken),
                }),
                "ext.integration.steam.jobs" => Ok(new
                {
                    requestId = HttpContext.TraceIdentifier,
                    result = await HandleJobsOperationAsync(payload, runtimeContext, cancellationToken),
                }),
                "ext.account.proxy-credentials.apply" => Ok(new
                {
                    requestId = HttpContext.TraceIdentifier,
                    result = new
                    {
                        accepted = true,
                        mode = "no-op",
                        reason = "steam integration runtime does not persist proxy credentials in adapter mode",
                        runtimeAccountId = runtimeContext.RuntimeAccountId,
                    },
                }),
                _ => BadRequest(new
                {
                    error = "unsupported action",
                    action = normalizedAction,
                })
            };
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "Worker integration action validation failed. Action: {Action}. TraceId: {TraceId}",
                normalizedAction,
                HttpContext.TraceIdentifier);
            return BadRequest(new
            {
                error = "validation_failed",
                message = exception.Message,
            });
        }
    }

    private async Task<object> HandleReadOperationAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var operation = ReadRequiredString(payload, "operation").Trim().ToLowerInvariant();
        return operation switch
        {
            "accounts.list" => await ReadAccountsListAsync(payload, runtimeContext, cancellationToken),
            "accounts.get" => await ReadAccountCardAsync(payload, runtimeContext, cancellationToken),
            "jobs.list" => await ReadJobsListAsync(payload, runtimeContext, cancellationToken),
            "jobs.details" => await ReadJobDetailsAsync(payload, runtimeContext, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported read operation `{operation}`.")
        };
    }

    private async Task<object> HandleJobsOperationAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var operation = ReadRequiredString(payload, "operation").Trim().ToLowerInvariant();
        return operation switch
        {
            "accounts.create" => await CreateAccountAsync(payload, runtimeContext, cancellationToken),
            "accounts.update" => await UpdateAccountAsync(payload, runtimeContext, cancellationToken),
            "accounts.archive" => await ArchiveAccountAsync(payload, runtimeContext, cancellationToken),
            "jobs.create" => await CreateJobAsync(payload, runtimeContext, cancellationToken),
            "jobs.cancel" => await CancelJobAsync(payload, runtimeContext, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported jobs operation `{operation}`.")
        };
    }

    private async Task<object> ReadAccountsListAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var filter = new AccountFilterRequest
        {
            Query = ReadOptionalString(payload, "query"),
            Page = ReadOptionalInt(payload, "page") ?? 1,
            PageSize = Math.Clamp(ReadOptionalInt(payload, "pageSize") ?? 50, 1, 200),
            Status = ParseAccountStatus(ReadOptionalString(payload, "status")),
        };

        var pageResult = await accountService.GetAsync(filter, cancellationToken);
        var runtimeItems = pageResult.Items
            .Where(item => IsOwnedByRuntime(item.Metadata, runtimeContext))
            .Select(MapAccount)
            .ToArray();

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            projectId = runtimeContext.ProjectId,
            storageNamespace = runtimeContext.StorageNamespace,
            items = runtimeItems,
            totalCount = runtimeItems.Length,
        };
    }

    private async Task<object> ReadAccountCardAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var accountId = ReadRequiredGuid(payload, "accountId");
        var account = await accountService.GetByIdAsync(accountId, cancellationToken);
        if (account is null || !IsOwnedByRuntime(account.Metadata, runtimeContext))
        {
            throw new InvalidOperationException("Account not found in current runtime scope.");
        }

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            account = MapAccount(account),
        };
    }

    private async Task<object> CreateAccountAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequired<AccountUpsertRequest>(payload, "account");
        if (string.IsNullOrWhiteSpace(request.LoginName))
        {
            throw new InvalidOperationException("account.loginName is required.");
        }

        request.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        request.Metadata["ddcrm.runtimeAccountId"] = runtimeContext.RuntimeAccountId;
        request.Metadata["ddcrm.projectId"] = runtimeContext.ProjectId;
        request.Metadata["ddcrm.storageNamespace"] = runtimeContext.StorageNamespace;
        request.Metadata["ddcrm.integration"] = "steam";

        var account = await accountService.CreateAsync(
            request,
            actorId: "ddcrm-integration",
            ip: null,
            cancellationToken);

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            account = MapAccount(account),
        };
    }

    private async Task<object> UpdateAccountAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var accountId = ReadRequiredGuid(payload, "accountId");
        var existing = await accountService.GetByIdAsync(accountId, cancellationToken);
        if (existing is null || !IsOwnedByRuntime(existing.Metadata, runtimeContext))
        {
            throw new InvalidOperationException("Account not found in current runtime scope.");
        }

        var request = DeserializeRequired<AccountUpsertRequest>(payload, "account");
        request.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        request.Metadata["ddcrm.runtimeAccountId"] = runtimeContext.RuntimeAccountId;
        request.Metadata["ddcrm.projectId"] = runtimeContext.ProjectId;
        request.Metadata["ddcrm.storageNamespace"] = runtimeContext.StorageNamespace;
        request.Metadata["ddcrm.integration"] = "steam";

        var updated = await accountService.UpdateAsync(
            accountId,
            request,
            actorId: "ddcrm-integration",
            ip: null,
            cancellationToken);
        if (updated is null)
        {
            throw new InvalidOperationException("Account update failed.");
        }

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            account = MapAccount(updated),
        };
    }

    private async Task<object> ArchiveAccountAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var accountId = ReadRequiredGuid(payload, "accountId");
        var existing = await accountService.GetByIdAsync(accountId, cancellationToken);
        if (existing is null || !IsOwnedByRuntime(existing.Metadata, runtimeContext))
        {
            throw new InvalidOperationException("Account not found in current runtime scope.");
        }

        var archived = await accountService.ArchiveAsync(
            accountId,
            actorId: "ddcrm-integration",
            ip: null,
            cancellationToken);

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            archived,
        };
    }

    private async Task<object> ReadJobsListAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(ReadOptionalInt(payload, "take") ?? 30, 1, 200);
        var runtimeAccountIds = await ResolveRuntimeAccountIdsAsync(runtimeContext, cancellationToken);
        var jobs = await jobService.GetRecentAsync(take, cancellationToken);
        var resultJobs = new List<object>();

        foreach (var job in jobs)
        {
            var items = await jobService.GetItemsAsync(job.Id, cancellationToken);
            if (items.Count == 0)
            {
                continue;
            }

            var ownedItems = items
                .Where(item => runtimeAccountIds.Contains(item.AccountId))
                .Select(MapJobItem)
                .ToArray();
            if (ownedItems.Length == 0)
            {
                continue;
            }

            resultJobs.Add(MapJob(job, ownedItems));
        }

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            jobs = resultJobs,
        };
    }

    private async Task<object> ReadJobDetailsAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var jobId = ReadRequiredGuid(payload, "jobId");
        var job = await jobService.GetByIdAsync(jobId, cancellationToken)
                  ?? throw new InvalidOperationException("Job not found.");

        var runtimeAccountIds = await ResolveRuntimeAccountIdsAsync(runtimeContext, cancellationToken);
        var items = await jobService.GetItemsAsync(jobId, cancellationToken);
        var ownedItems = items
            .Where(item => runtimeAccountIds.Contains(item.AccountId))
            .Select(MapJobItem)
            .ToArray();

        if (ownedItems.Length == 0)
        {
            throw new InvalidOperationException("Job is outside runtime scope.");
        }

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            job = MapJob(job, ownedItems),
        };
    }

    private async Task<object> CreateJobAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var request = DeserializeRequired<JobCreateRequest>(payload, "job");
        if (request.AccountIds.Count == 0)
        {
            throw new InvalidOperationException("job.accountIds must not be empty.");
        }

        foreach (var accountId in request.AccountIds)
        {
            var account = await accountService.GetByIdAsync(accountId, cancellationToken);
            if (account is null || !IsOwnedByRuntime(account.Metadata, runtimeContext))
            {
                throw new InvalidOperationException($"Account `{accountId}` is outside runtime scope.");
            }
        }

        var job = await jobService.CreateAsync(
            request,
            actorId: "ddcrm-integration",
            ip: null,
            cancellationToken);
        backgroundJobs.Enqueue<HangfireJobExecutor>(x => x.ExecuteAsync(job.Id, CancellationToken.None));

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            job = MapJob(job, items: null),
        };
    }

    private async Task<object> CancelJobAsync(
        JsonElement payload,
        RuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var jobId = ReadRequiredGuid(payload, "jobId");
        var runtimeAccountIds = await ResolveRuntimeAccountIdsAsync(runtimeContext, cancellationToken);
        var items = await jobService.GetItemsAsync(jobId, cancellationToken);
        if (items.Count == 0 || items.All(item => !runtimeAccountIds.Contains(item.AccountId)))
        {
            throw new InvalidOperationException("Job not found in current runtime scope.");
        }

        var canceled = await jobService.CancelAsync(
            jobId,
            actorId: "ddcrm-integration",
            ip: null,
            cancellationToken);

        return new
        {
            runtimeAccountId = runtimeContext.RuntimeAccountId,
            canceled,
        };
    }

    private async Task<HashSet<Guid>> ResolveRuntimeAccountIdsAsync(RuntimeContext runtimeContext, CancellationToken cancellationToken)
    {
        var accounts = await dbContext.SteamAccounts
            .AsNoTracking()
            .Select(x => new { x.Id, x.MetadataJson })
            .ToListAsync(cancellationToken);

        var result = new HashSet<Guid>();
        foreach (var account in accounts)
        {
            var metadata = DeserializeMetadata(account.MetadataJson);
            if (IsOwnedByRuntime(metadata, runtimeContext))
            {
                result.Add(account.Id);
            }
        }

        return result;
    }

    private static object MapAccount(AccountDto account)
    {
        return new
        {
            account.Id,
            account.LoginName,
            account.DisplayName,
            account.SteamId64,
            account.Email,
            account.PhoneMasked,
            account.Proxy,
            account.FolderName,
            status = account.Status.ToString(),
            account.Note,
            account.Tags,
            account.Metadata,
            account.CreatedAt,
            account.UpdatedAt,
        };
    }

    private static object MapJob(JobDto job, IReadOnlyCollection<object>? items)
    {
        return new
        {
            job.Id,
            type = job.Type.ToString(),
            status = job.Status.ToString(),
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            job.TotalCount,
            job.SuccessCount,
            job.FailureCount,
            job.DryRun,
            job.Payload,
            items,
        };
    }

    private static object MapJobItem(JobItemDto item)
    {
        return new
        {
            item.Id,
            item.JobId,
            item.AccountId,
            status = item.Status.ToString(),
            item.Attempt,
            item.ErrorText,
            item.ReasonCode,
            item.Retryable,
            item.StartedAt,
            item.FinishedAt,
            item.Request,
            item.Result,
        };
    }

    private static AccountStatus? ParseAccountStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<AccountStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsOwnedByRuntime(IReadOnlyDictionary<string, string> metadata, RuntimeContext context)
    {
        if (!metadata.TryGetValue("ddcrm.runtimeAccountId", out var runtimeAccountId)
            || !string.Equals(runtimeAccountId, context.RuntimeAccountId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!metadata.TryGetValue("ddcrm.projectId", out var projectId)
            || !string.Equals(projectId, context.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> DeserializeMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static T DeserializeRequired<T>(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"payload.{propertyName} is required.");
        }

        try
        {
            var value = property.Deserialize<T>(WorkerJsonOptions);
            if (value is null)
            {
                throw new InvalidOperationException($"payload.{propertyName} is null.");
            }

            return value;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Invalid payload.{propertyName}: {exception.Message}");
        }
    }

    private static JsonSerializerOptions CreateWorkerJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string ReadRequiredString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"payload.{propertyName} is required.");
        }

        return property.GetString()!.Trim();
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? ReadOptionalInt(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue)
            ? intValue
            : null;
    }

    private static Guid ReadRequiredGuid(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"payload.{propertyName} is required.");
        }

        return property.ValueKind switch
        {
            JsonValueKind.String when Guid.TryParse(property.GetString(), out var value) => value,
            _ => throw new InvalidOperationException($"payload.{propertyName} must be GUID."),
        };
    }

    private bool TryRequireWorkerServiceToken(out IActionResult? unauthorizedResult)
    {
        unauthorizedResult = null;

        var authEnabled = configuration.GetValue<bool?>("WORKER_API_SERVICE_AUTH_ENABLED")
            ?? configuration.GetValue("DDCRM_SERVICE_AUTH_ENABLED", true);
        if (!authEnabled)
        {
            return true;
        }

        var acceptedTokensCsv = configuration["WORKER_API_SERVICE_AUTH_ACCEPTED_TOKENS"]
            ?? configuration["DDCRM_SERVICE_AUTH_TOKENS"]
            ?? string.Empty;

        var acceptedTokens = acceptedTokensCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        if (acceptedTokens.Count == 0)
        {
            unauthorizedResult = StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "WORKER_API_SERVICE_AUTH_ACCEPTED_TOKENS is empty",
            });
            return false;
        }

        var serviceToken = Request.Headers["X-Service-Token"].ToString();
        if (string.IsNullOrWhiteSpace(serviceToken) || !acceptedTokens.Contains(serviceToken))
        {
            unauthorizedResult = Unauthorized(new { error = "invalid service token" });
            return false;
        }

        return true;
    }

    private RuntimeContext ResolveRuntimeContext()
    {
        var runtimeAccountId = configuration["DDCRM_WORKER_ACCOUNT_ID"];
        if (string.IsNullOrWhiteSpace(runtimeAccountId))
        {
            runtimeAccountId = "runtime-account-unbound";
        }

        var projectId = configuration["DDCRM_WORKER_PROJECT_ID"];
        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = "project-unbound";
        }

        var storageNamespace = configuration["STEAM_ACCOUNTS_STORAGE_NAMESPACE"];
        if (string.IsNullOrWhiteSpace(storageNamespace))
        {
            var accountIdN = runtimeAccountId.Replace("-", string.Empty, StringComparison.Ordinal);
            storageNamespace = $"steam-{projectId}-{accountIdN}";
        }

        var shortAccount = runtimeAccountId.Length <= 8
            ? runtimeAccountId
            : runtimeAccountId[..8];

        return new RuntimeContext(runtimeAccountId, shortAccount, projectId, storageNamespace);
    }

    private sealed record RuntimeContext(
        string RuntimeAccountId,
        string RuntimeAccountIdShort,
        string ProjectId,
        string StorageNamespace);
}

public sealed record WorkerActionRequest(JsonElement? Payload);
