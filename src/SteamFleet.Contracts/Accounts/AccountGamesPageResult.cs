namespace SteamFleet.Contracts.Accounts;

public sealed class AccountGamesPageResult
{
    public IReadOnlyCollection<AccountGameDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
