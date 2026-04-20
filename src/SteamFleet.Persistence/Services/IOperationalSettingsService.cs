using SteamFleet.Contracts.Settings;

namespace SteamFleet.Persistence.Services;

public interface IOperationalSettingsService
{
    Task<OperationalSafetySettingsDto> GetAsync(CancellationToken cancellationToken = default);
    Task<OperationalSafetySettingsDto> UpdateAsync(
        UpdateOperationalSafetySettingsRequest request,
        string actorId,
        string? ip,
        CancellationToken cancellationToken = default);
    bool IsSensitiveJobType(SteamFleet.Contracts.Enums.JobType jobType);
}

