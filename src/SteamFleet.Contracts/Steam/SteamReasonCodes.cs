namespace SteamFleet.Contracts.Steam;

public static class SteamReasonCodes
{
    public const string None = "None";
    public const string AuthSessionMissing = "AuthSessionMissing";
    public const string InvalidCredentials = "InvalidCredentials";
    public const string AccessDenied = "AccessDenied";
    public const string AuthThrottled = "AuthThrottled";
    public const string SessionReplaced = "SessionReplaced";
    public const string GuardPending = "GuardPending";
    public const string AntiBotBlocked = "AntiBotBlocked";
    public const string EndpointRejected = "EndpointRejected";
    public const string Timeout = "Timeout";
    public const string CooldownActive = "CooldownActive";
    public const string Canceled = "Canceled";
    public const string Expired = "Expired";
    public const string AuthFailed = "AuthFailed";
    public const string DuplicateAccount = "DuplicateAccount";
    public const string InvalidInviteLink = "InvalidInviteLink";
    public const string TargetAccountMissing = "TargetAccountMissing";
    public const string SourceAccountMissing = "SourceAccountMissing";
    public const string FamilyNotFound = "FamilyNotFound";
    public const string FamilySyncFailed = "FamilySyncFailed";
    public const string ExternalDataUnavailable = "ExternalDataUnavailable";
    public const string Unknown = "Unknown";
}
