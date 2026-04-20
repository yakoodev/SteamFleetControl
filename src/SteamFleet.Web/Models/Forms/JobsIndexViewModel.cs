using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;

namespace SteamFleet.Web.Models.Forms;

public sealed class JobsIndexViewModel
{
    public JobType? Type { get; init; }
    public JobStatus? Status { get; init; }
    public string? Query { get; init; }
    public required IReadOnlyCollection<JobDto> Jobs { get; init; }
}
