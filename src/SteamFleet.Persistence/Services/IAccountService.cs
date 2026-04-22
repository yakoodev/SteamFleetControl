using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Steam;

namespace SteamFleet.Persistence.Services;

public interface IAccountService
{
    Task<AccountsPageResult> GetAsync(AccountFilterRequest request, CancellationToken cancellationToken = default);
    Task<AccountDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AccountDto> CreateAsync(AccountUpsertRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountDto?> UpdateAsync(Guid id, AccountUpsertRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<bool> ArchiveAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountImportResult> ImportAsync(Stream stream, string fileName, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<byte[]> ExportCsvAsync(AccountFilterRequest filter, CancellationToken cancellationToken = default);
    Task<SteamAuthResult> AuthenticateAsync(Guid id, AccountAuthenticateRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountQrOnboardingStartResult> StartQrOnboardingAsync(string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountQrOnboardingPollResult> PollQrOnboardingAsync(Guid flowId, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task CancelQrOnboardingAsync(Guid flowId, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamQrAuthStartResult> StartQrAuthenticationAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamQrAuthPollResult> PollQrAuthenticationAsync(Guid id, Guid flowId, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task CancelQrAuthenticationAsync(Guid id, Guid flowId, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamSessionValidationResult> ValidateSessionAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamSessionInfo> RefreshSessionAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<GuardConfirmationsResultDto> GetGuardConfirmationsAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptGuardConfirmationAsync(Guid id, ulong confirmationId, ulong confirmationKey, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> DenyGuardConfirmationAsync(Guid id, ulong confirmationId, ulong confirmationKey, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptGuardConfirmationsBatchAsync(Guid id, IReadOnlyCollection<GuardConfirmationRefDto> confirmations, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<GuardLinkStateDto> StartGuardLinkAsync(Guid id, GuardLinkStartRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<GuardLinkStateDto> ProvideGuardPhoneAsync(Guid id, GuardLinkPhoneRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<GuardLinkStateDto> FinalizeGuardLinkAsync(Guid id, GuardLinkFinalizeRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> RemoveAuthenticatorAsync(Guid id, RemoveAuthenticatorRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountPasswordChangeResult> ChangePasswordAsync(Guid id, AccountPasswordChangeRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> DeauthorizeAllSessionsAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountGamesPageResult> RefreshGamesAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountGamesPageResult> GetGamesAsync(Guid id, AccountGamesScope scope, string? query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<AccountFamilySnapshotDto> SyncFamilyFromSteamAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountFamilySnapshotDto> GetFamilySnapshotAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> InviteToFamilyAsync(Guid id, FamilyInviteRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptFamilyInviteAsync(Guid id, FamilyAcceptInviteRequest request, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<FriendInviteLinkDto?> GetFriendInviteLinkAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FriendInviteLinkDto> SyncFriendInviteLinkAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<SteamOperationResult> AcceptFriendInviteAsync(Guid id, string inviteUrl, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountFriendsSnapshotDto> RefreshFriendsAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountFriendsSnapshotDto> GetFriendsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AccountDto?> AssignParentAsync(Guid id, Guid parentAccountId, string actorId, string? ip, CancellationToken cancellationToken = default);
    Task<AccountDto?> RemoveParentAsync(Guid id, string actorId, string? ip, CancellationToken cancellationToken = default);
}
