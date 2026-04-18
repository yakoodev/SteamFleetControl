using SteamFleet.Contracts.Enums;

namespace SteamFleet.Contracts.Accounts;

public sealed class AccountUpsertRequest
{
    public required string LoginName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? PhoneMasked { get; set; }
    public string? Password { get; set; }
    public string? SharedSecret { get; set; }
    public string? IdentitySecret { get; set; }
    public string? SessionPayload { get; set; }
    public string? RecoveryPayload { get; set; }
    public string? Proxy { get; set; }
    public string? FolderName { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Note { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AccountStatus Status { get; set; } = AccountStatus.Active;
}
