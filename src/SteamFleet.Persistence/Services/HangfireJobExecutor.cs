using Microsoft.Extensions.Logging;

namespace SteamFleet.Persistence.Services;

public sealed class HangfireJobExecutor(IJobService jobService, ILogger<HangfireJobExecutor> logger)
{
    public async Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Executing SteamFleet job {JobId}", jobId);
        await jobService.ExecuteAsync(jobId, cancellationToken);
    }
}
