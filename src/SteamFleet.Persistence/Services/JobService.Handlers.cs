using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;

namespace SteamFleet.Persistence.Services;

public sealed partial class JobService
{
    private async Task<JobItemProcessingResult> ProcessItemAsync(
        JobType type,
        SteamAccount account,
        Dictionary<string, string> payload,
        string actorId,
        CancellationToken cancellationToken)
    {
        return type switch
        {
            JobType.SessionValidate => JobItemProcessingResult.FromResult(await HandleValidateSessionAsync(account, cancellationToken)),
            JobType.SessionRefresh => JobItemProcessingResult.FromResult(await HandleRefreshSessionAsync(account, cancellationToken)),
            JobType.ProfileUpdate => JobItemProcessingResult.FromResult(await HandleProfileUpdateAsync(account, payload, cancellationToken)),
            JobType.PrivacyUpdate => JobItemProcessingResult.FromResult(await HandlePrivacyUpdateAsync(account, payload, cancellationToken)),
            JobType.AvatarUpdate => JobItemProcessingResult.FromResult(await HandleAvatarUpdateAsync(account, payload, cancellationToken)),
            JobType.TagsAssign => JobItemProcessingResult.FromResult(await HandleTagsAssignAsync(account, payload, cancellationToken)),
            JobType.GroupMove => JobItemProcessingResult.FromResult(await HandleGroupMoveAsync(account, payload, cancellationToken)),
            JobType.AddNote => JobItemProcessingResult.FromResult(await HandleAddNoteAsync(account, payload, cancellationToken)),
            JobType.PasswordChange => await HandlePasswordChangeAsync(account, payload, actorId, cancellationToken),
            JobType.SessionsDeauthorize => JobItemProcessingResult.FromResult(await HandleSessionsDeauthorizeAsync(account, actorId, cancellationToken)),
            JobType.FriendsAddByInvite => JobItemProcessingResult.FromResult(await HandleFriendsAddByInviteAsync(account, payload, actorId, cancellationToken)),
            JobType.FriendsConnectFamilyMain => JobItemProcessingResult.FromResult(await HandleFriendsAddByInviteAsync(account, payload, actorId, cancellationToken)),
            _ => JobItemProcessingResult.Fail($"Unsupported job type {type}", SteamReasonCodes.EndpointRejected)
        };
    }

    private async Task<SteamOperationResult> HandleValidateSessionAsync(SteamAccount account, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        var validation = await steamGateway.ValidateSessionAsync(ensuredSession.SessionPayload, cancellationToken);
        account.LastCheckAt = DateTimeOffset.UtcNow;
        return new SteamOperationResult
        {
            Success = validation.IsValid,
            ErrorMessage = validation.IsValid ? null : validation.Reason,
            Data = new Dictionary<string, string>
            {
                ["isValid"] = validation.IsValid.ToString(),
                ["reason"] = validation.Reason ?? string.Empty
            }
        };
    }

    private async Task<SteamOperationResult> HandleRefreshSessionAsync(SteamAccount account, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        SteamSessionInfo refreshed;
        try
        {
            refreshed = await steamGateway.RefreshSessionAsync(ensuredSession.SessionPayload, cancellationToken);
        }
        catch (Exception ex)
        {
            var reauth = await EnsureSessionPayloadAsync(account, cancellationToken, forceReauth: true);
            if (!reauth.Success || string.IsNullOrWhiteSpace(reauth.SessionPayload))
            {
                return new SteamOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Refresh failed: {ex.Message}. Re-auth failed: {reauth.ErrorMessage}"
                };
            }

            return new SteamOperationResult
            {
                Success = true,
                Data = new Dictionary<string, string>
                {
                    ["mode"] = "reauth",
                    ["expiresAt"] = account.LastSuccessAt?.ToString("O") ?? string.Empty
                }
            };
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(refreshed.CookiePayload ?? ensuredSession.SessionPayload);
        account.Secret.EncryptionVersion = cryptoService.Version;

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string>
            {
                ["expiresAt"] = refreshed.ExpiresAt?.ToString("O") ?? string.Empty
            }
        };
    }

    private async Task<SteamOperationResult> HandleProfileUpdateAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        var profile = new SteamProfileData
        {
            DisplayName = payload.GetValueOrDefault("displayName"),
            Summary = payload.GetValueOrDefault("summary"),
            RealName = payload.GetValueOrDefault("realName"),
            Country = payload.GetValueOrDefault("country"),
            State = payload.GetValueOrDefault("state"),
            City = payload.GetValueOrDefault("city"),
            CustomUrl = payload.GetValueOrDefault("customUrl")
        };

        var result = await steamGateway.UpdateProfileAsync(ensuredSession.SessionPayload, profile, cancellationToken);
        if (result.Success)
        {
            account.DisplayName = profile.DisplayName ?? account.DisplayName;
        }

        return result;
    }

    private async Task<SteamOperationResult> HandlePrivacyUpdateAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        var settings = new SteamPrivacySettings
        {
            ProfilePrivate = bool.TryParse(payload.GetValueOrDefault("profilePrivate"), out var profilePrivate) && profilePrivate,
            FriendsPrivate = bool.TryParse(payload.GetValueOrDefault("friendsPrivate"), out var friendsPrivate) && friendsPrivate,
            InventoryPrivate = bool.TryParse(payload.GetValueOrDefault("inventoryPrivate"), out var inventoryPrivate) && inventoryPrivate
        };

        return await steamGateway.UpdatePrivacySettingsAsync(ensuredSession.SessionPayload, settings, cancellationToken);
    }

    private async Task<SteamOperationResult> HandleAvatarUpdateAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = ensuredSession.ErrorMessage };
        }

        if (!payload.TryGetValue("avatarBase64", out var avatarBase64) || string.IsNullOrWhiteSpace(avatarBase64))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "avatarBase64 payload is required" };
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(avatarBase64);
        }
        catch
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "avatarBase64 is invalid base64" };
        }

        return await steamGateway.UpdateAvatarAsync(ensuredSession.SessionPayload, bytes, "avatar.png", cancellationToken);
    }

    private async Task<SteamOperationResult> HandleTagsAssignAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("tags", out var tagsRaw) || string.IsNullOrWhiteSpace(tagsRaw))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "tags payload is required" };
        }

        var tags = tagsRaw.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tags.Length == 0)
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "No tags supplied" };
        }

        account.TagLinks.Clear();
        var existingTags = await dbContext.SteamAccountTags.Where(t => tags.Contains(t.Name)).ToListAsync(cancellationToken);
        var missing = tags.Except(existingTags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in missing)
        {
            var tag = new SteamAccountTag { Name = tagName };
            existingTags.Add(tag);
            await dbContext.SteamAccountTags.AddAsync(tag, cancellationToken);
        }

        foreach (var tag in existingTags)
        {
            account.TagLinks.Add(new SteamAccountTagLink
            {
                AccountId = account.Id,
                TagId = tag.Id,
                Account = account,
                Tag = tag
            });
        }

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string> { ["tags"] = string.Join('|', tags) }
        };
    }

    private async Task<SteamOperationResult> HandleGroupMoveAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("folder", out var folderName) || string.IsNullOrWhiteSpace(folderName))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "folder payload is required" };
        }

        var folder = await dbContext.Folders.FirstOrDefaultAsync(x => x.Name == folderName, cancellationToken);
        if (folder is null)
        {
            folder = new Folder { Name = folderName };
            await dbContext.Folders.AddAsync(folder, cancellationToken);
        }

        account.Folder = folder;
        account.FolderId = folder.Id;

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string> { ["folder"] = folderName }
        };
    }

    private Task<SteamOperationResult> HandleAddNoteAsync(SteamAccount account, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        payload.TryGetValue("note", out var note);

        if (string.IsNullOrWhiteSpace(note))
        {
            return Task.FromResult(new SteamOperationResult { Success = false, ErrorMessage = "note payload is required" });
        }

        account.Note = string.IsNullOrWhiteSpace(account.Note)
            ? note
            : $"{account.Note}{Environment.NewLine}{DateTimeOffset.UtcNow:O}: {note}";

        return Task.FromResult(new SteamOperationResult { Success = true });
    }

    private async Task<JobItemProcessingResult> HandlePasswordChangeAsync(
        SteamAccount account,
        Dictionary<string, string> payload,
        string actorId,
        CancellationToken cancellationToken)
    {
        var currentPassword = payload.TryGetValue("currentPassword", out var payloadCurrentPassword) &&
                              !string.IsNullOrWhiteSpace(payloadCurrentPassword)
            ? payloadCurrentPassword
            : await DecryptSecretAsync(account, account.Secret?.EncryptedPassword, "password", cancellationToken);

        if (string.IsNullOrWhiteSpace(currentPassword))
        {
            return JobItemProcessingResult.Fail(
                "Current password is not configured.",
                SteamReasonCodes.AuthSessionMissing);
        }

        var fixedPassword = payload.GetValueOrDefault("newPassword");
        if (string.IsNullOrWhiteSpace(fixedPassword) &&
            payload.TryGetValue("encryptedNewPassword", out var encryptedFixedPassword) &&
            !string.IsNullOrWhiteSpace(encryptedFixedPassword))
        {
            fixedPassword = cryptoService.Decrypt(encryptedFixedPassword);
        }
        var generated = string.IsNullOrWhiteSpace(fixedPassword);
        var passwordLength = int.TryParse(payload.GetValueOrDefault("generateLength"), out var parsedLength)
            ? Math.Clamp(parsedLength, 12, 64)
            : 20;
        var nextPassword = generated ? GenerateStrongPassword(passwordLength) : fixedPassword!;

        var deauthorizeAfterChange =
            bool.TryParse(payload.GetValueOrDefault("deauthorizeAfterChange"), out var parsedDeauthorize) &&
            parsedDeauthorize;

        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return JobItemProcessingResult.Fail(
                ensuredSession.ErrorMessage ?? "Session is unavailable.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        var change = await steamGateway.ChangePasswordAsync(
            ensuredSession.SessionPayload,
            currentPassword,
            nextPassword,
            cancellationToken: cancellationToken);

        if (!change.Success)
        {
            return JobItemProcessingResult.Fail(
                change.ErrorMessage ?? "Password change failed.",
                change.ReasonCode,
                change.Retryable);
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedPassword = cryptoService.Encrypt(nextPassword);
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.Status = AccountStatus.Active;

        var deauthorized = false;
        string? deauthorizeWarning = null;
        string? deauthorizeReasonCode = null;
        var deauthorizeRetryable = false;
        if (deauthorizeAfterChange)
        {
            var deauthResult = await steamGateway.DeauthorizeAllSessionsAsync(ensuredSession.SessionPayload, cancellationToken);
            if (deauthResult.Success)
            {
                account.Secret.EncryptedSessionPayload = null;
                account.Status = AccountStatus.RequiresRelogin;
                deauthorized = true;
            }
            else
            {
                deauthorizeWarning = string.IsNullOrWhiteSpace(deauthResult.ErrorMessage)
                    ? "Password changed but Steam did not confirm deauthorization."
                    : $"Password changed but deauthorize failed: {deauthResult.ErrorMessage}";
                deauthorizeReasonCode = deauthResult.ReasonCode;
                deauthorizeRetryable = deauthResult.Retryable;
                account.LastErrorAt = DateTimeOffset.UtcNow;
                account.Status = AccountStatus.Active;
            }
        }

        var result = new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string>
            {
                ["passwordChanged"] = true.ToString(),
                ["generated"] = generated.ToString(),
                ["deauthorized"] = deauthorized.ToString()
            }
        };
        if (!string.IsNullOrWhiteSpace(deauthorizeWarning))
        {
            result.Data["deauthorizeWarning"] = deauthorizeWarning;
            if (!string.IsNullOrWhiteSpace(deauthorizeReasonCode))
            {
                result.Data["deauthorizeReasonCode"] = deauthorizeReasonCode;
            }

            result.Data["deauthorizeRetryable"] = deauthorizeRetryable.ToString();
        }

        await auditService.WriteAsync(
            AuditEventType.PasswordChanged,
            "steam_account",
            account.Id.ToString(),
            actorId,
            null,
            new Dictionary<string, string>
            {
                ["deauthorized"] = deauthorized.ToString(),
                ["generated"] = generated.ToString(),
                ["deauthorizeWarning"] = (!string.IsNullOrWhiteSpace(deauthorizeWarning)).ToString(),
                ["source"] = "job"
            },
            cancellationToken);

        return new JobItemProcessingResult
        {
            Result = result,
            SensitivePassword = nextPassword
        };
    }

    private async Task<SteamOperationResult> HandleSessionsDeauthorizeAsync(
        SteamAccount account,
        string actorId,
        CancellationToken cancellationToken)
    {
        var ensuredSession = await EnsureSessionPayloadAsync(account, cancellationToken);
        if (!ensuredSession.Success || string.IsNullOrWhiteSpace(ensuredSession.SessionPayload))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ensuredSession.ErrorMessage,
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        var deauth = await steamGateway.DeauthorizeAllSessionsAsync(ensuredSession.SessionPayload, cancellationToken);
        if (!deauth.Success)
        {
            return deauth;
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };
        account.Secret.EncryptedSessionPayload = null;
        account.Secret.EncryptionVersion = cryptoService.Version;
        account.Status = AccountStatus.RequiresRelogin;

        await auditService.WriteAsync(
            AuditEventType.SessionsDeauthorized,
            "steam_account",
            account.Id.ToString(),
            actorId,
            null,
            new Dictionary<string, string> { ["source"] = "job" },
            cancellationToken);

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string> { ["deauthorized"] = true.ToString() }
        };
    }

    private async Task<SteamOperationResult> HandleFriendsAddByInviteAsync(
        SteamAccount sourceAccount,
        Dictionary<string, string> payload,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("targetAccountId", out var targetAccountIdRaw) ||
            !Guid.TryParse(targetAccountIdRaw, out var targetAccountId))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "targetAccountId is missing for friends connect item.",
                ReasonCode = SteamReasonCodes.TargetAccountMissing
            };
        }

        var targetAccount = await dbContext.SteamAccounts
            .Include(x => x.Secret)
            .FirstOrDefaultAsync(x => x.Id == targetAccountId, cancellationToken);
        if (targetAccount is null)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Target account not found.",
                ReasonCode = SteamReasonCodes.TargetAccountMissing
            };
        }

        var sourceSession = await EnsureSessionPayloadAsync(sourceAccount, cancellationToken);
        if (!sourceSession.Success || string.IsNullOrWhiteSpace(sourceSession.SessionPayload))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = sourceSession.ErrorMessage,
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        var targetSession = await EnsureSessionPayloadAsync(targetAccount, cancellationToken);
        if (!targetSession.Success || string.IsNullOrWhiteSpace(targetSession.SessionPayload))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = targetSession.ErrorMessage,
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        SteamFriendInviteLink invite;
        try
        {
            invite = await steamGateway.GetFriendInviteLinkAsync(sourceSession.SessionPayload, cancellationToken);
        }
        catch (SteamGatewayOperationException ex)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = ex.ReasonCode ?? SteamReasonCodes.Unknown,
                Retryable = ex.Retryable
            };
        }
        catch (Exception ex)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = SteamReasonCodes.Unknown,
                Retryable = true
            };
        }

        if (string.IsNullOrWhiteSpace(invite.InviteUrl))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Invite URL was not produced by source account.",
                ReasonCode = SteamReasonCodes.EndpointRejected
            };
        }

        SteamOperationResult accept;
        try
        {
            accept = await steamGateway.AcceptFriendInviteAsync(targetSession.SessionPayload, invite.InviteUrl, cancellationToken);
        }
        catch (SteamGatewayOperationException ex)
        {
            accept = new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = ex.ReasonCode ?? SteamReasonCodes.Unknown,
                Retryable = ex.Retryable
            };
        }
        catch (Exception ex)
        {
            accept = new SteamOperationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReasonCode = SteamReasonCodes.Unknown,
                Retryable = true
            };
        }

        if (!accept.Success)
        {
            targetAccount.LastErrorAt = DateTimeOffset.UtcNow;
            targetAccount.Status = accept.ReasonCode is SteamReasonCodes.AuthSessionMissing or SteamReasonCodes.GuardPending
                ? AccountStatus.RequiresRelogin
                : AccountStatus.Error;

            await auditService.WriteAsync(
                AuditEventType.FriendConnectFailed,
                "steam_account",
                targetAccount.Id.ToString(),
                actorId,
                null,
                new Dictionary<string, string>
                {
                    ["sourceAccountId"] = sourceAccount.Id.ToString(),
                    ["targetAccountId"] = targetAccount.Id.ToString(),
                    ["reasonCode"] = accept.ReasonCode ?? SteamReasonCodes.Unknown,
                    ["retryable"] = accept.Retryable.ToString()
                },
                cancellationToken);
            return accept;
        }

        sourceAccount.LastSuccessAt = DateTimeOffset.UtcNow;
        sourceAccount.LastErrorAt = null;
        if (sourceAccount.Status is AccountStatus.Error or AccountStatus.RequiresRelogin)
        {
            sourceAccount.Status = AccountStatus.Active;
        }

        targetAccount.LastSuccessAt = DateTimeOffset.UtcNow;
        targetAccount.LastErrorAt = null;
        if (targetAccount.Status is AccountStatus.Error or AccountStatus.RequiresRelogin)
        {
            targetAccount.Status = AccountStatus.Active;
        }

        await auditService.WriteAsync(
            AuditEventType.FriendInviteAccepted,
            "steam_account",
            targetAccount.Id.ToString(),
            actorId,
            null,
            new Dictionary<string, string>
            {
                ["sourceAccountId"] = sourceAccount.Id.ToString(),
                ["targetAccountId"] = targetAccount.Id.ToString()
            },
            cancellationToken);

        return new SteamOperationResult
        {
            Success = true,
            ReasonCode = SteamReasonCodes.None,
            Data = new Dictionary<string, string>
            {
                ["sourceAccountId"] = sourceAccount.Id.ToString(),
                ["targetAccountId"] = targetAccount.Id.ToString(),
                ["inviteCode"] = invite.InviteCode
            }
        };
    }
}

