namespace SteamFleet.Contracts.Accounts;

public sealed class GuardLinkStartRequest
{
    public string? PhoneNumber { get; set; }
    public string? PhoneCountryCode { get; set; }
}

public sealed class GuardLinkPhoneRequest
{
    public required string PhoneNumber { get; set; }
    public string? PhoneCountryCode { get; set; }
}

public sealed class GuardLinkFinalizeRequest
{
    public required string SmsCode { get; set; }
}

public sealed class RemoveAuthenticatorRequest
{
    public int Scheme { get; set; } = 1;
}

public sealed class GuardLinkStateDto
{
    public string Step { get; set; } = "None";
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReasonCode { get; set; }
    public bool Retryable { get; set; }
    public bool FullyEnrolled { get; set; }
    public string? PhoneNumberHint { get; set; }
    public string? ConfirmationEmailAddress { get; set; }
    public string? DeviceId { get; set; }
    public string? RevocationCode { get; set; }
    public string? SerialNumber { get; set; }
    public string? TokenGid { get; set; }
    public string? Uri { get; set; }
}
