using SteamFleet.Contracts.Jobs;

namespace SteamFleet.Web.Models.Forms;

public sealed class JobDetailsViewModel
{
    public required JobDto Job { get; init; }
    public required IReadOnlyCollection<JobItemDto> Items { get; init; }
    public required IReadOnlyDictionary<Guid, JobAccountInfo> Accounts { get; init; }

    public JobAccountInfo? TryGetAccount(Guid accountId)
        => Accounts.TryGetValue(accountId, out var account) ? account : null;

    public sealed record JobAccountInfo(string LoginName, string? DisplayName, string Status);
}
