using SteamFleet.Contracts.Jobs;

namespace SteamFleet.Persistence.Services;

public interface IJobService
{
    Task<JobDto> CreateAsync(JobCreateRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<JobDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<JobDto>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<JobItemDto>> GetItemsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<byte[]?> DownloadSensitiveReportOnceAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task ExecuteAsync(Guid id, CancellationToken cancellationToken = default);
}
