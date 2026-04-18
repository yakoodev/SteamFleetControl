namespace SteamFleet.Contracts.Accounts;

public sealed class AccountsPageResult
{
    public required IReadOnlyCollection<AccountDto> Items { get; init; }
    public int TotalCount { get; init; }
}
