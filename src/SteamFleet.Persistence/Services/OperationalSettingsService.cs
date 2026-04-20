using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Settings;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;

namespace SteamFleet.Persistence.Services;

public sealed class OperationalSettingsService(
    SteamFleetDbContext dbContext,
    IAuditService auditService) : IOperationalSettingsService
{
    private const string SafetySettingsKey = "operations.safety.v1";

    public async Task<OperationalSafetySettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == SafetySettingsKey, cancellationToken);

        var defaults = Default();
        if (entity is null || string.IsNullOrWhiteSpace(entity.ValueJson))
        {
            return defaults;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<UpdateOperationalSafetySettingsRequest>(entity.ValueJson, JsonSerialization.Defaults);
            var normalized = Normalize(stored ?? new UpdateOperationalSafetySettingsRequest());
            return new OperationalSafetySettingsDto
            {
                SafeModeEnabled = normalized.SafeModeEnabled,
                BlockManualSensitiveDuringCooldown = normalized.BlockManualSensitiveDuringCooldown,
                DefaultJobParallelism = normalized.DefaultJobParallelism,
                DefaultJobRetryCount = normalized.DefaultJobRetryCount,
                MaxSensitiveParallelism = normalized.MaxSensitiveParallelism,
                MaxSensitiveAccountsPerJob = normalized.MaxSensitiveAccountsPerJob,
                UpdatedAt = entity.UpdatedAt
            };
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    public async Task<OperationalSafetySettingsDto> UpdateAsync(
        UpdateOperationalSafetySettingsRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request);
        var valueJson = JsonSerializer.Serialize(normalized, JsonSerialization.Defaults);

        var entity = await dbContext.SystemSettings
            .FirstOrDefaultAsync(x => x.Key == SafetySettingsKey, cancellationToken);

        if (entity is null)
        {
            entity = new SystemSetting
            {
                Key = SafetySettingsKey,
                ValueJson = valueJson
            };
            await dbContext.SystemSettings.AddAsync(entity, cancellationToken);
        }
        else
        {
            entity.ValueJson = valueJson;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SystemSettingsUpdated,
            "system_settings",
            entity.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string>
            {
                ["safeModeEnabled"] = normalized.SafeModeEnabled.ToString(),
                ["blockManualSensitiveDuringCooldown"] = normalized.BlockManualSensitiveDuringCooldown.ToString(),
                ["defaultJobParallelism"] = normalized.DefaultJobParallelism.ToString(),
                ["defaultJobRetryCount"] = normalized.DefaultJobRetryCount.ToString(),
                ["maxSensitiveParallelism"] = normalized.MaxSensitiveParallelism.ToString(),
                ["maxSensitiveAccountsPerJob"] = normalized.MaxSensitiveAccountsPerJob.ToString()
            },
            cancellationToken);

        return new OperationalSafetySettingsDto
        {
            SafeModeEnabled = normalized.SafeModeEnabled,
            BlockManualSensitiveDuringCooldown = normalized.BlockManualSensitiveDuringCooldown,
            DefaultJobParallelism = normalized.DefaultJobParallelism,
            DefaultJobRetryCount = normalized.DefaultJobRetryCount,
            MaxSensitiveParallelism = normalized.MaxSensitiveParallelism,
            MaxSensitiveAccountsPerJob = normalized.MaxSensitiveAccountsPerJob,
            UpdatedAt = entity.UpdatedAt
        };
    }

    public bool IsSensitiveJobType(JobType jobType)
    {
        return jobType is
            JobType.ProfileUpdate or
            JobType.PrivacyUpdate or
            JobType.AvatarUpdate or
            JobType.SessionValidate or
            JobType.SessionRefresh or
            JobType.PasswordChange or
            JobType.SessionsDeauthorize or
            JobType.FriendsAddByInvite or
            JobType.FriendsConnectFamilyMain;
    }

    private static OperationalSafetySettingsDto Default()
    {
        return new OperationalSafetySettingsDto
        {
            SafeModeEnabled = true,
            BlockManualSensitiveDuringCooldown = false,
            DefaultJobParallelism = 3,
            DefaultJobRetryCount = 1,
            MaxSensitiveParallelism = 2,
            MaxSensitiveAccountsPerJob = 50
        };
    }

    private static UpdateOperationalSafetySettingsRequest Normalize(UpdateOperationalSafetySettingsRequest request)
    {
        var defaultSettings = Default();
        var normalized = new UpdateOperationalSafetySettingsRequest
        {
            SafeModeEnabled = request.SafeModeEnabled,
            BlockManualSensitiveDuringCooldown = request.BlockManualSensitiveDuringCooldown,
            DefaultJobParallelism = request.DefaultJobParallelism <= 0
                ? defaultSettings.DefaultJobParallelism
                : request.DefaultJobParallelism,
            DefaultJobRetryCount = request.DefaultJobRetryCount < 0
                ? defaultSettings.DefaultJobRetryCount
                : request.DefaultJobRetryCount,
            MaxSensitiveParallelism = request.MaxSensitiveParallelism <= 0
                ? defaultSettings.MaxSensitiveParallelism
                : request.MaxSensitiveParallelism,
            MaxSensitiveAccountsPerJob = request.MaxSensitiveAccountsPerJob <= 0
                ? defaultSettings.MaxSensitiveAccountsPerJob
                : request.MaxSensitiveAccountsPerJob
        };

        normalized.DefaultJobParallelism = Math.Clamp(normalized.DefaultJobParallelism, 1, 50);
        normalized.DefaultJobRetryCount = Math.Clamp(normalized.DefaultJobRetryCount, 0, 10);
        normalized.MaxSensitiveParallelism = Math.Clamp(normalized.MaxSensitiveParallelism, 1, 20);
        normalized.MaxSensitiveAccountsPerJob = Math.Clamp(normalized.MaxSensitiveAccountsPerJob, 1, 1000);
        if (normalized.MaxSensitiveParallelism > normalized.DefaultJobParallelism)
        {
            normalized.MaxSensitiveParallelism = normalized.DefaultJobParallelism;
        }

        return normalized;
    }
}

