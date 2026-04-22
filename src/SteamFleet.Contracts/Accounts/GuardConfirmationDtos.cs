namespace SteamFleet.Contracts.Accounts;

public sealed class GuardConfirmationDto
{
    public ulong Id { get; set; }
    public ulong Key { get; set; }
    public ulong CreatorId { get; set; }
    public string? Headline { get; set; }
    public List<string> Summary { get; set; } = [];
    public string? AcceptText { get; set; }
    public string? CancelText { get; set; }
    public string? IconUrl { get; set; }
    public string Type { get; set; } = "Unknown";
}

public sealed class GuardConfirmationsResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReasonCode { get; set; }
    public bool Retryable { get; set; }
    public bool NeedAuthentication { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<GuardConfirmationDto> Confirmations { get; set; } = [];
}

public sealed class GuardConfirmationRefDto
{
    public ulong Id { get; set; }
    public ulong Key { get; set; }
}

public sealed class GuardConfirmationsBatchRequest
{
    public List<GuardConfirmationRefDto> Confirmations { get; set; } = [];
}
