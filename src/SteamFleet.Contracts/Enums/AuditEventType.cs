namespace SteamFleet.Contracts.Enums;

public enum AuditEventType
{
    LoginSucceeded,
    LoginFailed,
    SecretRead,
    SecretUpdated,
    AccountCreated,
    AccountUpdated,
    AccountArchived,
    PasswordChanged,
    SessionsDeauthorized,
    FamilyParentAssigned,
    FamilyParentRemoved,
    GamesRefreshed,
    FriendInviteSynced,
    FriendInviteAccepted,
    FriendConnectFailed,
    SensitiveReportDownloaded,
    JobCreated,
    JobStarted,
    JobCompleted,
    JobCanceled,
    JobItemFailed,
    SystemError
}
