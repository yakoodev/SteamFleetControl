namespace SteamFleet.Contracts.Accounts;

public sealed class AccountImportResult
{
    public int Total { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public List<string> Errors { get; set; } = [];
}
