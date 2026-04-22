using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using SteamFleet.Contracts.Accounts;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Steam;
using SteamFleet.Domain.Entities;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Security;

namespace SteamFleet.Persistence.Services;

public sealed partial class AccountService
{
    private static AccountQrOnboardingStatus MapOnboardingStatus(SteamQrAuthStatus status)
    {
        return status switch
        {
            SteamQrAuthStatus.Pending => AccountQrOnboardingStatus.Pending,
            SteamQrAuthStatus.Completed => AccountQrOnboardingStatus.Completed,
            SteamQrAuthStatus.Canceled => AccountQrOnboardingStatus.Canceled,
            SteamQrAuthStatus.Expired => AccountQrOnboardingStatus.Expired,
            SteamQrAuthStatus.Failed => AccountQrOnboardingStatus.Failed,
            _ => AccountQrOnboardingStatus.Failed
        };
    }

    private static string MapOnboardingReasonCode(SteamQrAuthStatus status, string? errorMessage)
    {
        return status switch
        {
            SteamQrAuthStatus.Pending => SteamReasonCodes.None,
            SteamQrAuthStatus.Completed => SteamReasonCodes.None,
            SteamQrAuthStatus.Canceled => SteamReasonCodes.Canceled,
            SteamQrAuthStatus.Expired => SteamReasonCodes.Expired,
            SteamQrAuthStatus.Failed when IsTimeoutMessage(errorMessage) => SteamReasonCodes.Timeout,
            SteamQrAuthStatus.Failed => SteamReasonCodes.AuthFailed,
            _ => SteamReasonCodes.Unknown
        };
    }

    private static bool IsTimeoutMessage(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildQrImageDataUrl(string challengeUrl)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrData);
        var png = qrCode.GetGraphic(12);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    private static string GenerateStrongPassword(int length = 20)
    {
        if (length < 12)
        {
            length = 12;
        }

        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        // Steam password change flow is sensitive to certain symbols.
        // Use conservative symbols and avoid placing them at the edges.
        const string special = "-_";
        var alphaNum = upper + lower + digits;

        var password = new char[length];
        password[0] = alphaNum[RandomNumberGenerator.GetInt32(alphaNum.Length)];
        password[^1] = alphaNum[RandomNumberGenerator.GetInt32(alphaNum.Length)];

        var availableSlots = Enumerable.Range(1, length - 2).ToList();
        static int TakeRandomSlot(List<int> slots)
        {
            var index = RandomNumberGenerator.GetInt32(slots.Count);
            var value = slots[index];
            slots.RemoveAt(index);
            return value;
        }

        password[TakeRandomSlot(availableSlots)] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        password[TakeRandomSlot(availableSlots)] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        password[TakeRandomSlot(availableSlots)] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        password[TakeRandomSlot(availableSlots)] = special[RandomNumberGenerator.GetInt32(special.Length)];

        foreach (var slot in availableSlots)
        {
            password[slot] = alphaNum[RandomNumberGenerator.GetInt32(alphaNum.Length)];
        }

        return new string(password);
    }

    private async Task ApplyUpsertAsync(SteamAccount account, AccountUpsertRequest request, string actorId, CancellationToken cancellationToken)
    {
        account.DisplayName = request.DisplayName;
        account.Email = request.Email;
        account.PhoneMasked = request.PhoneMasked;
        account.Note = request.Note;
        account.Proxy = request.Proxy;
        account.Status = request.Status;
        account.UpdatedBy = actorId;

        if (request.Metadata.Count > 0)
        {
            account.MetadataJson = JsonSerializer.Serialize(request.Metadata, JsonSerialization.Defaults);
        }

        if (!string.IsNullOrWhiteSpace(request.FolderName))
        {
            var folderName = request.FolderName.Trim();
            var folder = await dbContext.Folders.FirstOrDefaultAsync(x => x.Name == folderName, cancellationToken);
            if (folder is null)
            {
                folder = new Folder { Name = folderName };
                await dbContext.Folders.AddAsync(folder, cancellationToken);
            }

            account.Folder = folder;
            account.FolderId = folder.Id;
        }

        var normalizedTags = request.Tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedTags.Length > 0)
        {
            account.TagLinks.Clear();

            var existingTags = await dbContext.SteamAccountTags
                .Where(x => normalizedTags.Contains(x.Name))
                .ToListAsync(cancellationToken);

            var newTagNames = normalizedTags.Except(existingTags.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var newTagName in newTagNames)
            {
                var tag = new SteamAccountTag { Name = newTagName };
                existingTags.Add(tag);
                await dbContext.SteamAccountTags.AddAsync(tag, cancellationToken);
            }

            foreach (var tag in existingTags)
            {
                account.TagLinks.Add(new SteamAccountTagLink { Account = account, Tag = tag, AccountId = account.Id, TagId = tag.Id });
            }
        }

        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            account.Secret.EncryptedPassword = cryptoService.Encrypt(request.Password);
        }

        if (!string.IsNullOrWhiteSpace(request.SharedSecret))
        {
            account.Secret.EncryptedSharedSecret = cryptoService.Encrypt(request.SharedSecret);
        }

        if (!string.IsNullOrWhiteSpace(request.IdentitySecret))
        {
            account.Secret.EncryptedIdentitySecret = cryptoService.Encrypt(request.IdentitySecret);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionPayload))
        {
            account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(request.SessionPayload);
        }

        if (!string.IsNullOrWhiteSpace(request.RecoveryPayload))
        {
            account.Secret.EncryptedRecoveryPayload = cryptoService.Encrypt(request.RecoveryPayload);
            BackfillGuardSecretFieldsFromPayload(account.Secret, request.RecoveryPayload, cryptoService);
        }

        account.Secret.EncryptionVersion = cryptoService.Version;
    }

    private AccountDto MapAccountDto(SteamAccount entity, int familyCount)
    {
        var metadata = JsonSerialization.DeserializeDictionary(entity.MetadataJson);
        RemoveSensitiveMetadataForOutput(metadata);

        return new AccountDto
        {
            Id = entity.Id,
            LoginName = entity.LoginName,
            DisplayName = entity.DisplayName,
            SteamId64 = entity.SteamId64,
            ProfileUrl = entity.ProfileUrl,
            Email = entity.Email,
            PhoneMasked = entity.PhoneMasked,
            Note = entity.Note,
            Proxy = entity.Proxy,
            FolderId = entity.FolderId,
            FolderName = entity.Folder?.Name,
            IsExternal = entity.IsExternal,
            ExternalSource = entity.ExternalSource,
            SteamFamilyId = entity.SteamFamilyId,
            SteamFamilyRole = entity.SteamFamilyRole,
            IsFamilyOrganizer = entity.IsFamilyOrganizer,
            FamilySyncedAt = entity.FamilySyncedAt,
            FamilyAccountsCount = familyCount,
            RiskLevel = entity.RiskLevel,
            AuthFailStreak = entity.AuthFailStreak,
            RiskSignalStreak = entity.RiskSignalStreak,
            LastRiskReasonCode = entity.LastRiskReasonCode,
            LastRiskAt = entity.LastRiskAt,
            AutoRetryAfter = entity.AutoRetryAfter,
            LastSensitiveOpAt = entity.LastSensitiveOpAt,
            Status = entity.Status,
            GamesLastSyncAt = entity.GamesLastSyncAt,
            GamesCount = entity.GamesCount,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            LastCheckAt = entity.LastCheckAt,
            LastSuccessAt = entity.LastSuccessAt,
            LastErrorAt = entity.LastErrorAt,
            CreatedBy = entity.CreatedBy,
            UpdatedBy = entity.UpdatedBy,
            Metadata = metadata,
            Tags = entity.TagLinks
                .Where(x => x.Tag is not null)
                .Select(x => x.Tag!.Name)
                .OrderBy(x => x)
                .ToList()
        };
    }

    private async Task<string?> GetSessionPayloadAsync(SteamAccount account, string actorId, string? ip, CancellationToken cancellationToken)
    {
        if (account.Secret is null)
        {
            return null;
        }

        await auditService.WriteAsync(
            AuditEventType.SecretRead,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["field"] = "session_payload" },
            cancellationToken);

        return cryptoService.Decrypt(account.Secret.EncryptedSessionPayload);
    }

    private async Task<string?> ReadSecretAsync(
        SteamAccount account,
        string? encryptedValue,
        string fieldName,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
        {
            return null;
        }

        await auditService.WriteAsync(
            AuditEventType.SecretRead,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["field"] = fieldName },
            cancellationToken);

        return cryptoService.Decrypt(encryptedValue);
    }

    private async Task ApplyAuthResultAsync(
        SteamAccount account,
        SteamAuthResult authResult,
        string? plaintextPassword,
        string? plaintextSharedSecret,
        string? plaintextIdentitySecret,
        string actorId,
        string? ip,
        CancellationToken cancellationToken)
    {
        account.Secret ??= new SteamAccountSecret { AccountId = account.Id };

        if (!string.IsNullOrWhiteSpace(plaintextPassword))
        {
            account.Secret.EncryptedPassword = cryptoService.Encrypt(plaintextPassword);
        }

        if (!string.IsNullOrWhiteSpace(plaintextSharedSecret))
        {
            account.Secret.EncryptedSharedSecret = cryptoService.Encrypt(plaintextSharedSecret);
        }

        if (!string.IsNullOrWhiteSpace(plaintextIdentitySecret))
        {
            account.Secret.EncryptedIdentitySecret = cryptoService.Encrypt(plaintextIdentitySecret);
        }

        if (!string.IsNullOrWhiteSpace(authResult.Session.CookiePayload))
        {
            account.Secret.EncryptedSessionPayload = cryptoService.Encrypt(authResult.Session.CookiePayload);
        }

        if (!string.IsNullOrWhiteSpace(authResult.GuardData))
        {
            account.Secret.EncryptedRecoveryPayload = cryptoService.Encrypt(authResult.GuardData);
            BackfillGuardSecretFieldsFromPayload(account.Secret, authResult.GuardData, cryptoService);
        }

        account.Secret.EncryptionVersion = cryptoService.Version;

        if (!string.IsNullOrWhiteSpace(authResult.SteamId64))
        {
            account.SteamId64 = authResult.SteamId64;
        }

        if (!string.IsNullOrWhiteSpace(authResult.AccountName))
        {
            account.DisplayName = authResult.AccountName;
        }

        account.LastCheckAt = DateTimeOffset.UtcNow;
        account.LastSuccessAt = DateTimeOffset.UtcNow;
        account.LastErrorAt = null;
        account.Status = AccountStatus.Active;
        account.UpdatedBy = actorId;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            AuditEventType.SecretUpdated,
            "steam_account_secret",
            account.Id.ToString(),
            actorId,
            ip,
            new Dictionary<string, string> { ["operation"] = "authenticate" },
            cancellationToken);
    }

    private static void BackfillGuardSecretFieldsFromPayload(
        SteamAccountSecret secret,
        string payload,
        ISecretCryptoService crypto)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject node)
            {
                return;
            }

            SetIfPresent(node, "device_id", x => secret.EncryptedDeviceId = crypto.Encrypt(x));
            SetIfPresent(node, "revocation_code", x => secret.EncryptedRevocationCode = crypto.Encrypt(x));
            SetIfPresent(node, "serial_number", x => secret.EncryptedSerialNumber = crypto.Encrypt(x));
            SetIfPresent(node, "token_gid", x => secret.EncryptedTokenGid = crypto.Encrypt(x));
            SetIfPresent(node, "uri", x => secret.EncryptedUri = crypto.Encrypt(x));
            SetIfPresent(node, "shared_secret", x => secret.EncryptedSharedSecret = crypto.Encrypt(x));
            SetIfPresent(node, "identity_secret", x => secret.EncryptedIdentitySecret = crypto.Encrypt(x));

            if (node["fully_enrolled"] is JsonValue enrolledNode)
            {
                if (enrolledNode.TryGetValue<bool>(out var enrolled))
                {
                    secret.GuardFullyEnrolled = enrolled;
                }
                else if (enrolledNode.TryGetValue<string>(out var enrolledText) &&
                         bool.TryParse(enrolledText, out var parsed))
                {
                    secret.GuardFullyEnrolled = parsed;
                }
            }
        }
        catch
        {
            // ignored: payload can be non-JSON on legacy records
        }
    }

    private static void SetIfPresent(JsonObject node, string key, Action<string> setter)
    {
        var raw = node[key]?.GetValue<string?>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        setter(raw.Trim());
    }

    private async Task<List<AccountUpsertRequest>> ParseImportAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await new StreamReader(stream).ReadToEndAsync(cancellationToken);
            return JsonSerializer.Deserialize<List<AccountUpsertRequest>>(json, JsonSerialization.Defaults) ?? [];
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.ToLowerInvariant().Trim(),
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim
        });

        var records = csv.GetRecords<dynamic>().ToList();
        var list = new List<AccountUpsertRequest>(records.Count);

        foreach (var record in records)
        {
            var dict = (IDictionary<string, object>)record;
            dict.TryGetValue("login", out var login);
            if (login is null || string.IsNullOrWhiteSpace(login.ToString()))
            {
                continue;
            }

            dict.TryGetValue("displayname", out var displayName);
            dict.TryGetValue("password", out var password);
            dict.TryGetValue("email", out var email);
            dict.TryGetValue("tags", out var tags);
            dict.TryGetValue("folder", out var folder);
            dict.TryGetValue("note", out var note);
            dict.TryGetValue("proxy", out var proxy);

            list.Add(new AccountUpsertRequest
            {
                LoginName = login.ToString()!,
                DisplayName = displayName?.ToString(),
                Password = password?.ToString(),
                Email = email?.ToString(),
                FolderName = folder?.ToString(),
                Note = note?.ToString(),
                Proxy = proxy?.ToString(),
                Tags = tags?.ToString()?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? []
            });
        }

        return list;
    }
}
