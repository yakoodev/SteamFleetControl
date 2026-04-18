using SteamFleet.Contracts.Jobs;

namespace SteamFleet.Web.Models.Forms;

public sealed class JobDetailsViewModel
{
    public required JobDto Job { get; init; }
    public required IReadOnlyCollection<JobItemDto> Items { get; init; }
}
