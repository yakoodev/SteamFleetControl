namespace SteamFleet.Contracts.Steam;

public sealed class SteamCredentials
{
    public required string LoginName { get; set; }
    public required string Password { get; set; }
    public string? SharedSecret { get; set; }
    public string? IdentitySecret { get; set; }
    public string? GuardCode { get; set; }
    public string? GuardData { get; set; }
    public bool AllowDeviceConfirmation { get; set; }
}

public sealed class SteamAuthResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SteamId64 { get; set; }
    public string? AccountName { get; set; }
    public string? GuardData { get; set; }
    public SteamSessionInfo Session { get; set; } = new();
}

public sealed class SteamSessionInfo
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? CookiePayload { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class SteamSessionValidationResult
{
    public bool IsValid { get; set; }
    public string? Reason { get; set; }
}

public enum SteamQrAuthStatus
{
    Pending,
    Completed,
    Failed,
    Canceled,
    Expired
}

public sealed class SteamQrAuthStartResult
{
    public Guid FlowId { get; set; }
    public string ChallengeUrl { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int PollingIntervalSeconds { get; set; }
}

public sealed class SteamQrAuthPollResult
{
    public Guid FlowId { get; set; }
    public SteamQrAuthStatus Status { get; set; }
    public string? ChallengeUrl { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? ErrorMessage { get; set; }
    public SteamAuthResult? AuthResult { get; set; }
}

public sealed class SteamProfileData
{
    public string? DisplayName { get; set; }
    public string? Summary { get; set; }
    public string? RealName { get; set; }
    public string? Country { get; set; }
    public string? State { get; set; }
    public string? City { get; set; }
    public string? CustomUrl { get; set; }
    public string? AvatarUrl { get; set; }
}

public sealed class SteamPrivacySettings
{
    public bool ProfilePrivate { get; set; }
    public bool FriendsPrivate { get; set; }
    public bool InventoryPrivate { get; set; }
}

public sealed class SteamOperationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReasonCode { get; set; }
    public bool Retryable { get; set; }
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum SteamGuardConfirmationType
{
    Unknown = 0,
    Trade = 2,
    MarketListing = 3,
    FeatureOptOut = 4,
    PhoneNumberChange = 5,
    AccountRecovery = 6
}

public sealed class SteamGuardConfirmation
{
    public ulong Id { get; set; }
    public ulong Key { get; set; }
    public ulong CreatorId { get; set; }
    public string? Headline { get; set; }
    public List<string> Summary { get; set; } = [];
    public string? AcceptText { get; set; }
    public string? CancelText { get; set; }
    public string? IconUrl { get; set; }
    public SteamGuardConfirmationType Type { get; set; } = SteamGuardConfirmationType.Unknown;
}

public sealed class SteamGuardConfirmationsResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReasonCode { get; set; }
    public bool Retryable { get; set; }
    public bool NeedAuthentication { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SteamGuardConfirmation> Confirmations { get; set; } = [];
}

public sealed class SteamGuardConfirmationRef
{
    public ulong Id { get; set; }
    public ulong Key { get; set; }
}

public enum SteamGuardLinkStep
{
    None = 0,
    NeedPhoneNumber = 1,
    NeedEmailConfirmation = 2,
    NeedSmsCode = 3,
    Completed = 4,
    Failed = 5
}

public sealed class SteamGuardLinkState
{
    public SteamGuardLinkStep Step { get; set; } = SteamGuardLinkStep.None;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReasonCode { get; set; }
    public bool Retryable { get; set; }
    public bool FullyEnrolled { get; set; }
    public string? PhoneNumberHint { get; set; }
    public string? ConfirmationEmailAddress { get; set; }
    public string? DeviceId { get; set; }
    public string? SharedSecret { get; set; }
    public string? IdentitySecret { get; set; }
    public string? RevocationCode { get; set; }
    public string? SerialNumber { get; set; }
    public string? TokenGid { get; set; }
    public string? Uri { get; set; }
    public string? RecoveryPayload { get; set; }
}

public sealed class SteamOwnedGame
{
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int PlaytimeMinutes { get; set; }
    public string? ImgIconUrl { get; set; }
}

public sealed class SteamOwnedGamesSnapshot
{
    public string? ProfileUrl { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SteamOwnedGame> Games { get; set; } = [];
}

public sealed class SteamFriendInviteLink
{
    public string InviteUrl { get; set; } = string.Empty;
    public string InviteCode { get; set; } = string.Empty;
    public string InviteToken { get; set; } = string.Empty;
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SteamFriend
{
    public string SteamId64 { get; set; } = string.Empty;
    public string? PersonaName { get; set; }
    public string? ProfileUrl { get; set; }
}

public sealed class SteamFriendsSnapshot
{
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SteamFriend> Friends { get; set; } = [];
}

public sealed class SteamFamilyMember
{
    public string SteamId64 { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public bool IsOrganizer { get; set; }
}

public sealed class SteamFamilySnapshot
{
    public string? FamilyId { get; set; }
    public string? SelfRole { get; set; }
    public bool IsOrganizer { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SteamFamilyMember> Members { get; set; } = [];
}

public sealed class SteamPublicMemberData
{
    public string SteamId64 { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ProfileUrl { get; set; }
    public bool IsPublic { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SteamOwnedGame> Games { get; set; } = [];
}

public interface ISteamAccountGateway
{
    Task<SteamAuthResult> AuthenticateAsync(SteamCredentials credentials, CancellationToken cancellationToken = default);
    Task<SteamQrAuthStartResult> StartQrAuthenticationAsync(CancellationToken cancellationToken = default);
    Task<SteamQrAuthPollResult> PollQrAuthenticationAsync(Guid flowId, CancellationToken cancellationToken = default);
    Task CancelQrAuthenticationAsync(Guid flowId, CancellationToken cancellationToken = default);
    Task<SteamSessionValidationResult> ValidateSessionAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamSessionInfo> RefreshSessionAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamProfileData> GetProfileAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> UpdateProfileAsync(string sessionPayload, SteamProfileData profileData, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> UpdateAvatarAsync(string sessionPayload, byte[] avatarBytes, string fileName, CancellationToken cancellationToken = default);
    Task<SteamPrivacySettings> GetPrivacySettingsAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> UpdatePrivacySettingsAsync(string sessionPayload, SteamPrivacySettings settings, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> ChangePasswordAsync(
        string sessionPayload,
        string currentPassword,
        string newPassword,
        string? confirmationCode = null,
        string? confirmationContext = null,
        CancellationToken cancellationToken = default);
    Task<SteamOperationResult> DeauthorizeAllSessionsAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamGuardConfirmationsResult> GetConfirmationsAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptConfirmationAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        ulong confirmationId,
        ulong confirmationKey,
        CancellationToken cancellationToken = default);
    Task<SteamOperationResult> DenyConfirmationAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        ulong confirmationId,
        ulong confirmationKey,
        CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptConfirmationsBatchAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        IReadOnlyCollection<SteamGuardConfirmationRef> confirmations,
        CancellationToken cancellationToken = default);
    Task<SteamGuardLinkState> StartAuthenticatorLinkAsync(
        string sessionPayload,
        string? phoneNumber = null,
        string? phoneCountryCode = null,
        CancellationToken cancellationToken = default);
    Task<SteamGuardLinkState> ProvidePhoneForLinkAsync(
        string sessionPayload,
        string phoneNumber,
        string? phoneCountryCode = null,
        CancellationToken cancellationToken = default);
    Task<SteamGuardLinkState> FinalizeAuthenticatorLinkAsync(
        string sessionPayload,
        string sharedSecret,
        string smsCode,
        CancellationToken cancellationToken = default);
    Task<SteamOperationResult> RemoveAuthenticatorAsync(
        string sessionPayload,
        string revocationCode,
        int scheme = 1,
        CancellationToken cancellationToken = default);
    Task<SteamOwnedGamesSnapshot> GetOwnedGamesSnapshotAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamFriendInviteLink> GetFriendInviteLinkAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptFriendInviteAsync(string sessionPayload, string inviteUrl, CancellationToken cancellationToken = default);
    Task<SteamFriendsSnapshot> GetFriendsSnapshotAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> InviteToFamilyGroupAsync(
        string sessionPayload,
        string targetSteamId64,
        bool inviteAsChild = true,
        CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptFamilyInviteAsync(
        string sessionPayload,
        string? sourceSteamId64 = null,
        CancellationToken cancellationToken = default);
    Task<SteamFamilySnapshot> GetFamilySnapshotAsync(string sessionPayload, CancellationToken cancellationToken = default);
    Task<SteamPublicMemberData> GetPublicMemberDataAsync(string steamId64, CancellationToken cancellationToken = default);
    Task<string?> ResolveSteamIdAsync(string loginName, CancellationToken cancellationToken = default);
}
