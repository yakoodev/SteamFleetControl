using SteamFleet.Contracts.Enums;

namespace SteamFleet.Contracts.Accounts;

public sealed class AccountFilterRequest
{
    public string? Query { get; set; }
    public AccountStatus? Status { get; set; }
    public string? Tag { get; set; }
    public string? FamilyGroup { get; set; }
    public Guid? FolderId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
