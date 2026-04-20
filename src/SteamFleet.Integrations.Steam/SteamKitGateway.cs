using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SteamFleet.Contracts.Steam;
using SteamFleet.Integrations.Steam.Options;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway(
    ILogger<SteamKitGateway> logger,
    IOptions<SteamGatewayOptions> optionsAccessor) : ISteamAccountGateway
{
    private static readonly Regex ProfilePathRegex = new(
        @"(?:https?://steamcommunity\.com)?(?<path>/(?:id|profiles)/[^/?#]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProfileEditConfigRegex = new(
        @"id=([""'])profile_edit_config\1[^>]*data-profile-edit=([""'])(?<json>.*?)\2",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SsrRenderContextRegex = new(
        @"window\.SSR\.renderContext\s*=\s*(?<json>\{.*?\})\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SsrRenderContextJsonParseRegex = new(
        @"window\.SSR\.renderContext\s*=\s*JSON\.parse\((?<quote>[""'])(?<json>.*?)\k<quote>\)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SessionIdRegex = new(
        @"g_sessionID\s*=\s*([""'])(?<value>[^""']+)\1",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HiddenInputRegex = new(
        @"<input[^>]*type\s*=\s*([""'])hidden\1[^>]*name\s*=\s*([""'])(?<name>[^""']+)\2[^>]*value\s*=\s*([""'])(?<value>.*?)\3[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlFormRegex = new(
        @"<form(?<attrs>[^>]*)>(?<content>.*?)</form>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex FormActionRegex = new(
        @"action\s*=\s*([""'])(?<value>.*?)\1",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex FormMethodRegex = new(
        @"method\s*=\s*([""'])(?<value>.*?)\1",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex WizardEndpointRegex = new(
        @"(?<url>(?:https?://help\.steampowered\.com)?/en/wizard/[A-Za-z0-9_/]+(?:\?[^""'\s<]+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StorePasswordEndpointRegex = new(
        @"(?<url>(?:https?://store\.steampowered\.com)?/[A-Za-z0-9/_\-?=&]*password[A-Za-z0-9/_\-?=&]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DirectProfileUrlRegex = new(
        @"https?://steamcommunity\.com/(?:id|profiles)/[^""'\s<]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RgGamesJsonRegex = new(
        @"(?:var|let|const)\s+rgGames\s*=\s*(?<json>\[.*?\])\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex RgGamesJsonParseRegex = new(
        @"(?:var|let|const)\s+rgGames\s*=\s*JSON\.parse\((?<quote>[""'])(?<json>.*?)\k<quote>\)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex LoginTitleRegex = new(
        @"<title>\s*sign in\s*</title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex QuickInviteLinkRegex = new(
        @"^https?://s\.team/p/(?<code>[^/?#]+)(?:/(?<token>[^/?#]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuickInviteLinkFromHtmlRegex = new(
        @"https?://s\.team/p/(?<code>[^/""'\s<]+)(?:/(?<token>[^/""'\s<]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuickInviteEscapedRegex = new(
        @"https?:\\\/\\\/s\.team\\\/p\\\/(?<code>[^\\\/""']+)(?:\\\/(?<token>[^\\\/""']+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuickInviteShortUrlJsonRegex = new(
        @"""short_url""\s*:\s*""(?<url>https?:\\?/\\?/s\.team\\?/p\\?/[A-Za-z0-9\-]+(?:\\?/[A-Za-z0-9_\-]+)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DataUserInfoAttributeRegex = new(
        @"data-userinfo=(?<quote>[""'])(?<json>.*?)\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SubmitPasswordChangeCallRegex = new(
        @"SubmitPasswordChange\(\s*'(?<s>\d+)'\s*,\s*(?<account>\d+)\s*,\s*'(?<login>[^']+)'\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly char[] FriendCodeReplacements =
    [
        'b', 'c', 'd', 'f', 'g', 'h', 'j', 'k',
        'm', 'n', 'p', 'q', 'r', 't', 'v', 'w'
    ];

    private readonly SteamGatewayOptions _options = optionsAccessor.Value;
    private readonly ConcurrentDictionary<Guid, QrFlowState> _qrFlows = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountFlowLocks = new(StringComparer.Ordinal);

    public async Task<SteamAuthResult> AuthenticateAsync(SteamCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.LoginName) || string.IsNullOrWhiteSpace(credentials.Password))
        {
            return new SteamAuthResult
            {
                Success = false,
                ErrorMessage = "LoginName and Password are required."
            };
        }

        try
        {
            return await ExecuteWithSteamClientAsync(
                async (steamClient, token) =>
                {
                    var authenticator = new SteamCredentialAuthenticator(credentials);
                    var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                    {
                        Username = credentials.LoginName,
                        Password = credentials.Password,
                        IsPersistentSession = true,
                        GuardData = credentials.GuardData,
                        WebsiteID = _options.WebsiteId,
                        DeviceFriendlyName = _options.DeviceFriendlyName,
                        PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                        Authenticator = authenticator
                    }).ConfigureAwait(false);

                    var pollResult = await authSession.PollingWaitForResultAsync(token).ConfigureAwait(false);
                    var steamId64 = authSession.SteamID.ConvertToUInt64().ToString(CultureInfo.InvariantCulture);

                    var bundle = new SteamSessionBundle
                    {
                        SteamId64 = steamId64,
                        AccountName = pollResult.AccountName,
                        LoginName = credentials.LoginName,
                        AccessToken = pollResult.AccessToken,
                        RefreshToken = pollResult.RefreshToken,
                        GuardData = pollResult.NewGuardData ?? credentials.GuardData,
                        SessionId = GenerateSessionId(),
                        IssuedAt = DateTimeOffset.UtcNow,
                        ExpiresAt = TryGetJwtExpiry(pollResult.AccessToken)
                    };

                    return new SteamAuthResult
                    {
                        Success = true,
                        SteamId64 = steamId64,
                        AccountName = pollResult.AccountName,
                        GuardData = bundle.GuardData,
                        Session = new SteamSessionInfo
                        {
                            AccessToken = bundle.AccessToken,
                            RefreshToken = bundle.RefreshToken,
                            CookiePayload = SerializeBundle(bundle),
                            ExpiresAt = bundle.ExpiresAt
                        }
                    };
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            logger.LogWarning("Steam auth failed for {Login}. Result={Result}", credentials.LoginName, ex.Result);
            return new SteamAuthResult
            {
                Success = false,
                ErrorMessage = MapAuthenticationError(ex.Result)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Steam auth failed for {Login}", credentials.LoginName);
            return new SteamAuthResult
            {
                Success = false,
                ErrorMessage = "Steam authentication failed."
            };
        }
    }

    public async Task<SteamSessionValidationResult> ValidateSessionAsync(string sessionPayload, CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return new SteamSessionValidationResult
            {
                IsValid = false,
                Reason = parseError
            };
        }

        if (bundle.ExpiresAt is not null && bundle.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new SteamSessionValidationResult
            {
                IsValid = false,
                Reason = "Session expired."
            };
        }

        var result = await TryGetProfilePathAsync(bundle, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return new SteamSessionValidationResult
            {
                IsValid = false,
                Reason = result.ErrorMessage
            };
        }

        return new SteamSessionValidationResult { IsValid = true };
    }

    public async Task<SteamSessionInfo> RefreshSessionAsync(string sessionPayload, CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            throw new InvalidOperationException(parseError);
        }

        if (string.IsNullOrWhiteSpace(bundle.RefreshToken))
        {
            throw new InvalidOperationException("Session payload does not contain refresh token.");
        }

        if (!ulong.TryParse(bundle.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var steamIdRaw))
        {
            throw new InvalidOperationException("Session payload contains invalid SteamId.");
        }

        var tokens = await ExecuteWithSteamClientAsync(
            async (steamClient, _) =>
            {
                return await steamClient.Authentication.GenerateAccessTokenForAppAsync(
                    new SteamID(steamIdRaw),
                    bundle.RefreshToken,
                    allowRenewal: true).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        bundle.AccessToken = tokens.AccessToken;
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            bundle.RefreshToken = tokens.RefreshToken;
        }

        bundle.IssuedAt = DateTimeOffset.UtcNow;
        bundle.ExpiresAt = TryGetJwtExpiry(tokens.AccessToken);
        bundle.SessionId = GenerateSessionId();

        return new SteamSessionInfo
        {
            AccessToken = bundle.AccessToken,
            RefreshToken = bundle.RefreshToken,
            CookiePayload = SerializeBundle(bundle),
            ExpiresAt = bundle.ExpiresAt
        };
    }

    public async Task<SteamProfileData> GetProfileAsync(string sessionPayload, CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            throw new InvalidOperationException(parseError);
        }

        var profilePath = await GetProfilePathOrThrowAsync(bundle, cancellationToken).ConfigureAwait(false);
        var html = await GetPageHtmlAsync($"https://steamcommunity.com{profilePath}/edit/info", bundle, cancellationToken).ConfigureAwait(false);
        using var configJson = ParseProfileEditConfig(html);

        return new SteamProfileData
        {
            DisplayName = GetString(configJson.RootElement, "strPersonaName"),
            Summary = GetString(configJson.RootElement, "strSummary"),
            RealName = GetString(configJson.RootElement, "strRealName"),
            CustomUrl = GetString(configJson.RootElement, "strCustomURL"),
            Country = GetString(configJson.RootElement, "LocationData", "locCountryCode"),
            State = GetString(configJson.RootElement, "LocationData", "locStateCode"),
            City = GetString(configJson.RootElement, "LocationData", "locCityCode")
        };
    }

    public async Task<SteamOperationResult> UpdateProfileAsync(
        string sessionPayload,
        SteamProfileData profileData,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = parseError };
        }

        var profilePath = await GetProfilePathOrThrowAsync(bundle, cancellationToken).ConfigureAwait(false);
        var html = await GetPageHtmlAsync($"https://steamcommunity.com{profilePath}/edit/info", bundle, cancellationToken).ConfigureAwait(false);
        using var configJson = ParseProfileEditConfig(html);
        var root = configJson.RootElement;

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionID"] = bundle.SessionId,
            ["type"] = "profileSave",
            ["weblink_1_title"] = string.Empty,
            ["weblink_1_url"] = string.Empty,
            ["weblink_2_title"] = string.Empty,
            ["weblink_2_url"] = string.Empty,
            ["weblink_3_title"] = string.Empty,
            ["weblink_3_url"] = string.Empty,
            ["personaName"] = profileData.DisplayName ?? GetString(root, "strPersonaName") ?? string.Empty,
            ["real_name"] = profileData.RealName ?? GetString(root, "strRealName") ?? string.Empty,
            ["summary"] = profileData.Summary ?? GetString(root, "strSummary") ?? string.Empty,
            ["country"] = profileData.Country ?? GetString(root, "LocationData", "locCountryCode") ?? string.Empty,
            ["state"] = profileData.State ?? GetString(root, "LocationData", "locStateCode") ?? string.Empty,
            ["city"] = profileData.City ?? GetString(root, "LocationData", "locCityCode") ?? string.Empty,
            ["customURL"] = profileData.CustomUrl ?? GetString(root, "strCustomURL") ?? string.Empty,
            ["json"] = "1"
        };

        using var response = await SendCommunityFormAsync(
            HttpMethod.Post,
            $"https://steamcommunity.com{profilePath}/edit",
            bundle,
            form,
            MediaTypeNames.Application.FormUrlEncoded,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = $"Profile update failed: HTTP {(int)response.StatusCode}"
            };
        }

        try
        {
            using var resultJson = JsonDocument.Parse(body);
            var success = resultJson.RootElement.TryGetProperty("success", out var successValue)
                          && successValue.ValueKind is JsonValueKind.Number
                          && successValue.GetInt32() == 1;

            if (!success)
            {
                var errorMessage = resultJson.RootElement.TryGetProperty("errmsg", out var err)
                    ? err.GetString()
                    : "Steam profile update was not accepted.";

                return new SteamOperationResult { Success = false, ErrorMessage = errorMessage };
            }
        }
        catch (JsonException)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Steam profile update returned malformed response."
            };
        }

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string>
            {
                ["displayName"] = form["personaName"],
                ["summary"] = form["summary"],
                ["realName"] = form["real_name"],
                ["country"] = form["country"],
                ["state"] = form["state"],
                ["city"] = form["city"],
                ["customUrl"] = form["customURL"]
            }
        };
    }

    public async Task<SteamOperationResult> UpdateAvatarAsync(
        string sessionPayload,
        byte[] avatarBytes,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = parseError };
        }

        if (avatarBytes.Length == 0)
        {
            return new SteamOperationResult { Success = false, ErrorMessage = "Avatar payload is empty." };
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
        };

        using var content = new MultipartFormDataContent
        {
            { new StringContent(avatarBytes.Length.ToString(CultureInfo.InvariantCulture)), "MAX_FILE_SIZE" },
            { new StringContent("player_avatar_image"), "type" },
            { new StringContent(bundle.SteamId64), "sId" },
            { new StringContent(bundle.SessionId), "sessionid" },
            { new StringContent("1"), "doSub" },
            { new StringContent("1"), "json" }
        };

        var avatarContent = new ByteArrayContent(avatarBytes);
        avatarContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveAvatarContentType(fileName));
        content.Add(avatarContent, "avatar", ResolveAvatarFileName(fileName));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://steamcommunity.com/actions/FileUploader")
        {
            Content = content
        };
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Referrer = new Uri("https://steamcommunity.com/my/edit/avatar");
        request.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(bundle));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = $"Avatar upload failed: HTTP {(int)response.StatusCode}"
            };
        }

        try
        {
            using var result = JsonDocument.Parse(body);
            var success = result.RootElement.TryGetProperty("success", out var successValue)
                          && (successValue.ValueKind == JsonValueKind.True
                              || (successValue.ValueKind == JsonValueKind.Number && successValue.GetInt32() == 1));

            if (!success)
            {
                var errorMessage = result.RootElement.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : "Steam rejected avatar upload.";

                return new SteamOperationResult { Success = false, ErrorMessage = errorMessage };
            }

            var avatarUrl = result.RootElement.TryGetProperty("images", out var images)
                            && images.TryGetProperty("full", out var fullImage)
                ? fullImage.GetString()
                : null;

            return new SteamOperationResult
            {
                Success = true,
                Data = new Dictionary<string, string>
                {
                    ["fileName"] = ResolveAvatarFileName(fileName),
                    ["sizeBytes"] = avatarBytes.Length.ToString(CultureInfo.InvariantCulture),
                    ["avatarUrl"] = avatarUrl ?? string.Empty
                }
            };
        }
        catch (JsonException)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Avatar upload returned malformed JSON."
            };
        }
    }

    public async Task<SteamPrivacySettings> GetPrivacySettingsAsync(string sessionPayload, CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            throw new InvalidOperationException(parseError);
        }

        var profilePath = await GetProfilePathOrThrowAsync(bundle, cancellationToken).ConfigureAwait(false);
        var html = await GetPageHtmlAsync($"https://steamcommunity.com{profilePath}/edit/settings", bundle, cancellationToken).ConfigureAwait(false);
        using var configJson = ParseProfileEditConfig(html);

        var privacySettings = configJson.RootElement
            .GetProperty("Privacy")
            .GetProperty("PrivacySettings");

        var profileState = GetInt32(privacySettings, "PrivacyProfile", 3);
        var inventoryState = GetInt32(privacySettings, "PrivacyInventory", 3);
        var friendsState = GetInt32(privacySettings, "PrivacyFriendsList", 3);

        return new SteamPrivacySettings
        {
            ProfilePrivate = profileState != 3,
            InventoryPrivate = inventoryState != 3,
            FriendsPrivate = friendsState != 3
        };
    }

    public async Task<SteamOperationResult> UpdatePrivacySettingsAsync(
        string sessionPayload,
        SteamPrivacySettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = parseError };
        }

        var profilePath = await GetProfilePathOrThrowAsync(bundle, cancellationToken).ConfigureAwait(false);
        var html = await GetPageHtmlAsync($"https://steamcommunity.com{profilePath}/edit/settings", bundle, cancellationToken).ConfigureAwait(false);
        using var configJson = ParseProfileEditConfig(html);

        var privacyRoot = configJson.RootElement.GetProperty("Privacy");
        var existing = privacyRoot.GetProperty("PrivacySettings");

        var privacyPayload = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PrivacyProfile"] = settings.ProfilePrivate ? 1 : 3,
            ["PrivacyInventory"] = settings.InventoryPrivate ? 1 : 3,
            ["PrivacyInventoryGifts"] = settings.InventoryPrivate ? 1 : 3,
            ["PrivacyOwnedGames"] = GetInt32(existing, "PrivacyOwnedGames", settings.ProfilePrivate ? 1 : 3),
            ["PrivacyPlaytime"] = GetInt32(existing, "PrivacyPlaytime", settings.ProfilePrivate ? 1 : 3),
            ["PrivacyFriendsList"] = settings.FriendsPrivate ? 1 : 3
        };

        var commentPermission = settings.ProfilePrivate ? 2 : 0;
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionid"] = bundle.SessionId,
            ["Privacy"] = JsonSerializer.Serialize(privacyPayload, JsonOptions),
            ["eCommentPermission"] = commentPermission.ToString(CultureInfo.InvariantCulture)
        };

        using var response = await SendCommunityFormAsync(
            HttpMethod.Post,
            $"https://steamcommunity.com{profilePath}/ajaxsetprivacy",
            bundle,
            form,
            MediaTypeNames.Application.FormUrlEncoded,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = $"Privacy update failed: HTTP {(int)response.StatusCode}"
            };
        }

        try
        {
            using var result = JsonDocument.Parse(body);
            var success = result.RootElement.TryGetProperty("success", out var successValue)
                          && successValue.ValueKind == JsonValueKind.Number
                          && successValue.GetInt32() == 1;

            if (!success)
            {
                return new SteamOperationResult
                {
                    Success = false,
                    ErrorMessage = "Steam rejected privacy update."
                };
            }
        }
        catch (JsonException)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Privacy update returned malformed JSON."
            };
        }

        return new SteamOperationResult
        {
            Success = true,
            Data = new Dictionary<string, string>
            {
                ["profilePrivate"] = settings.ProfilePrivate.ToString(),
                ["friendsPrivate"] = settings.FriendsPrivate.ToString(),
                ["inventoryPrivate"] = settings.InventoryPrivate.ToString()
            }
        };
    }

    public async Task<SteamOperationResult> ChangePasswordAsync(
        string sessionPayload,
        string currentPassword,
        string newPassword,
        string? confirmationCode = null,
        string? confirmationContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = parseError };
        }

        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Current and new passwords are required.",
                ReasonCode = SteamReasonCodes.EndpointRejected
            };
        }

        return await ExecuteWithAccountFlowLockAsync(
                bundle,
                flowName: "confirmations",
                async token =>
                {
                    using var webSession = CreateWebSession(bundle);
                    await WarmupPasswordFlowAsync(webSession, token).ConfigureAwait(false);

                    var wizardAttempt = await TryChangePasswordViaSupportWizardAsync(
                            bundle,
                            currentPassword,
                            newPassword,
                            confirmationCode,
                            confirmationContext,
                            webSession,
                            token)
                        .ConfigureAwait(false);
                    if (wizardAttempt.Success)
                    {
                        return wizardAttempt;
                    }

                    if (string.Equals(wizardAttempt.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase))
                    {
                        return wizardAttempt;
                    }

                    if (!string.IsNullOrWhiteSpace(confirmationCode))
                    {
                        return wizardAttempt;
                    }

                    var storeAttempt = await TryChangePasswordViaStoreAccountAsync(bundle, currentPassword, newPassword, webSession, token)
                        .ConfigureAwait(false);
                    if (storeAttempt.Success)
                    {
                        return storeAttempt;
                    }

                    var reason = SelectPasswordFailureReason(wizardAttempt, storeAttempt);

                    var retryable = wizardAttempt.Retryable || storeAttempt.Retryable;
                    return Failure(
                        $"Password change failed. Wizard: {wizardAttempt.ErrorMessage}; Store: {storeAttempt.ErrorMessage}",
                        reason,
                        retryable);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SteamOperationResult> DeauthorizeAllSessionsAsync(
        string sessionPayload,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return new SteamOperationResult { Success = false, ErrorMessage = parseError };
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = "deauthorize",
            ["sessionid"] = bundle.SessionId
        };

        using var response = await SendStoreFormAsync(
            HttpMethod.Post,
            "https://store.steampowered.com/twofactor/manage_action",
            bundle,
            form,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var responsePath = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        if (!response.IsSuccessStatusCode)
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = $"Deauthorize request failed: HTTP {(int)response.StatusCode}"
            };
        }

        if (responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeStoreLoginPage(body))
        {
            return new SteamOperationResult
            {
                Success = false,
                ErrorMessage = "Store session is unauthorized.",
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        if (TryParseSuccessFromJson(body, out var errorText))
        {
            return new SteamOperationResult
            {
                Success = true,
                Data = new Dictionary<string, string>
                {
                    ["action"] = "deauthorize"
                }
            };
        }

        if (string.IsNullOrWhiteSpace(errorText) &&
            (responsePath.Contains("/twofactor/manage", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("twofactor", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("manage devices", StringComparison.OrdinalIgnoreCase)))
        {
            return new SteamOperationResult
            {
                Success = true,
                Data = new Dictionary<string, string>
                {
                    ["action"] = "deauthorize",
                    ["confirmation"] = "implicit"
                }
            };
        }

        return new SteamOperationResult
        {
            Success = false,
            ErrorMessage = string.IsNullOrWhiteSpace(errorText)
                ? "Steam did not confirm deauthorization."
                : errorText
        };
    }

    public async Task<SteamOwnedGamesSnapshot> GetOwnedGamesSnapshotAsync(
        string sessionPayload,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            throw new SteamGatewayOperationException(
                parseError,
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        try
        {
            var profilePath = await GetProfilePathOrThrowAsync(bundle, cancellationToken).ConfigureAwait(false);
            var profileUrl = $"https://steamcommunity.com{profilePath}";
            var html = await GetPageHtmlAsync($"{profileUrl}/games/?tab=all", bundle, cancellationToken).ConfigureAwait(false);
            if (LooksLikeGamesUnauthorizedPage(html))
            {
                throw new SteamGatewayOperationException(
                    "Steam community session is not authorized for games page.",
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true);
            }

            var games = new List<SteamOwnedGame>();
            if (TryParseSsrRenderContext(html, out var renderContext))
            {
                using (renderContext)
                {
                    CollectOwnedGames(renderContext.RootElement, games);
                    var profileUrlFromQueryData = CollectOwnedGamesFromQueryData(renderContext.RootElement, games);
                    if (string.IsNullOrWhiteSpace(profileUrl))
                    {
                        profileUrl = TryReadProfileUrl(renderContext.RootElement) ?? profileUrl;
                    }

                    if (!string.IsNullOrWhiteSpace(profileUrlFromQueryData) &&
                        (string.IsNullOrWhiteSpace(profileUrl) ||
                         profileUrl.EndsWith("/my", StringComparison.OrdinalIgnoreCase)))
                    {
                        profileUrl = profileUrlFromQueryData;
                    }
                }
            }

            if (games.Count == 0)
            {
                // Fallback for layouts where game data is serialized without SSR context.
                CollectOwnedGamesFromRawHtml(html, games);
            }

            if (string.IsNullOrWhiteSpace(profileUrl))
            {
                profileUrl = TryReadProfileUrlFromHtml(html) ?? profileUrl;
            }

            var dedup = games
                .GroupBy(x => x.AppId)
                .Select(g => g.OrderByDescending(x => x.PlaytimeMinutes).First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new SteamOwnedGamesSnapshot
            {
                ProfileUrl = profileUrl,
                SyncedAt = DateTimeOffset.UtcNow,
                Games = dedup
            };
        }
        catch (SteamGatewayOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to retrieve Steam owned games for steamId={SteamId}", bundle.SteamId64);
            throw ClassifyGatewayException(ex, "Could not refresh Steam owned games.");
        }
    }

    public async Task<SteamFriendInviteLink> GetFriendInviteLinkAsync(
        string sessionPayload,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            throw new SteamGatewayOperationException(
                parseError,
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        if (!ulong.TryParse(bundle.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId64))
        {
            throw new SteamGatewayOperationException(
                "Session payload contains invalid SteamId.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: false);
        }

        return await ExecuteWithAccountFlowLockAsync(
                bundle,
                flowName: "confirmations",
                async token =>
                {
                    try
                    {
                        try
                        {
                            var fromWeb = await TryGetFriendInviteLinkFromWebAsync(bundle, token).ConfigureAwait(false);
                            if (fromWeb is not null)
                            {
                                return fromWeb;
                            }
                        }
                        catch (SteamGatewayOperationException ex)
                        {
                            logger.LogInformation(
                                ex,
                                "Steam quick-invite web flow was unavailable; using deterministic invite-code fallback. steamId={SteamId} reason={ReasonCode}",
                                bundle.SteamId64,
                                ex.ReasonCode);
                        }

                        var inviteCode = CreateFriendCode(steamId64);
                        return new SteamFriendInviteLink
                        {
                            InviteCode = inviteCode,
                            InviteToken = string.Empty,
                            InviteUrl = $"https://s.team/p/{inviteCode}",
                            SyncedAt = DateTimeOffset.UtcNow
                        };
                    }
                    catch (SteamGatewayOperationException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to sync friend invite link for steamId={SteamId}", bundle.SteamId64);
                        throw ClassifyGatewayException(
                            ex,
                            "Could not sync Steam quick-invite link.");
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SteamOperationResult> AcceptFriendInviteAsync(
        string sessionPayload,
        string inviteUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return Failure(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (!TryParseQuickInviteLink(inviteUrl, out var sourceSteamId, out var inviteToken))
        {
            return Failure("Malformed quick-invite link.", SteamReasonCodes.InvalidInviteLink);
        }

        return await ExecuteWithAccountFlowLockAsync(
                bundle,
                flowName: "confirmations",
                async _ =>
                {
                    try
                    {
                        var webAttempt = await TryAcceptFriendInviteViaCommunityWebAsync(bundle, sourceSteamId, cancellationToken).ConfigureAwait(false);
                        if (webAttempt.Success)
                        {
                            webAttempt.Data["sourceSteamId"] = sourceSteamId.ToString(CultureInfo.InvariantCulture);
                            webAttempt.Data["inviteTokenTail"] = inviteToken.Length > 6 ? inviteToken[^6..] : inviteToken;
                            webAttempt.Data["inviteKind"] = string.IsNullOrWhiteSpace(inviteToken) ? "short_code" : "token";
                            return webAttempt;
                        }

                        var shouldFallbackToCm =
                            webAttempt.Retryable &&
                            (string.Equals(webAttempt.ReasonCode, SteamReasonCodes.EndpointRejected, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(webAttempt.ReasonCode, SteamReasonCodes.Unknown, StringComparison.OrdinalIgnoreCase));

                        if (!shouldFallbackToCm)
                        {
                            return webAttempt;
                        }

                        if (string.IsNullOrWhiteSpace(inviteToken))
                        {
                            await ExecuteWithLoggedOnSteamClientAsync(
                                    bundle,
                                    async (_, _, _, steamFriends, callbackManager, token) =>
                                    {
                                        var targetSteamId = new SteamID(sourceSteamId);
                                        var addResultTcs = new TaskCompletionSource<SteamFriends.FriendAddedCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
                                        callbackManager.Subscribe<SteamFriends.FriendAddedCallback>(callback =>
                                        {
                                            if (callback.SteamID == targetSteamId)
                                            {
                                                addResultTcs.TrySetResult(callback);
                                            }
                                        });

                                        steamFriends.AddFriend(targetSteamId);

                                        SteamFriends.FriendAddedCallback? callback = null;
                                        try
                                        {
                                            callback = await addResultTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                                        }
                                        catch (TimeoutException)
                                        {
                                            // Fallback to relationship check below.
                                        }

                                        var relationship = steamFriends.GetFriendRelationship(targetSteamId);
                                        var acceptedByRelationship =
                                            relationship is EFriendRelationship.Friend or
                                            EFriendRelationship.RequestInitiator or
                                            EFriendRelationship.RequestRecipient;
                                        var acceptedByCallback = callback is not null && callback.Result == EResult.OK;

                                        if (!acceptedByRelationship && !acceptedByCallback)
                                        {
                                            var callbackResult = callback?.Result.ToString() ?? "NoCallback";
                                            throw new InvalidOperationException(
                                                $"AddFriend failed: relationship={relationship}; callback={callbackResult}.");
                                        }

                                        return true;
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await ExecuteWithLoggedOnSteamClientAsync(
                                    bundle,
                                    async (_, _, unified, _, _, token) =>
                                    {
                                        var userAccount = unified.CreateService<UserAccount>();
                                        var response = await userAccount
                                            .RedeemFriendInviteToken(new CUserAccount_RedeemFriendInviteToken_Request
                                            {
                                                steamid = sourceSteamId,
                                                invite_token = inviteToken
                                            })
                                            .ToTask()
                                            .WaitAsync(token)
                                            .ConfigureAwait(false);

                                        if (response.Result != EResult.OK)
                                        {
                                            throw new InvalidOperationException($"RedeemFriendInviteToken failed: {response.Result}");
                                        }

                                        return true;
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        return new SteamOperationResult
                        {
                            Success = true,
                            ReasonCode = SteamReasonCodes.None,
                            Data = new Dictionary<string, string>
                            {
                                ["sourceSteamId"] = sourceSteamId.ToString(CultureInfo.InvariantCulture),
                                ["inviteTokenTail"] = inviteToken.Length > 6 ? inviteToken[^6..] : inviteToken,
                                ["inviteKind"] = string.IsNullOrWhiteSpace(inviteToken) ? "short_code" : "token"
                            }
                        };
                    }
                    catch (SteamGatewayOperationException ex)
                    {
                        logger.LogWarning(ex, "Friend invite redemption failed for steamId={SteamId}; reason={ReasonCode}", bundle.SteamId64, ex.ReasonCode);
                        return Failure(ex.Message, ex.ReasonCode ?? SteamReasonCodes.Unknown, ex.Retryable);
                    }
                    catch (TimeoutException tex)
                    {
                        logger.LogWarning(tex, "Friend invite redemption timeout for steamId={SteamId}", bundle.SteamId64);
                        return Failure("Friend invite redemption timed out.", SteamReasonCodes.Timeout, retryable: true);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("RateLimit", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning(ex, "Friend invite redemption rate-limited for steamId={SteamId}", bundle.SteamId64);
                        return Failure("Steam rate limited invite redemption.", SteamReasonCodes.AuthThrottled, retryable: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Friend invite redemption failed for steamId={SteamId}", bundle.SteamId64);
                        return Failure("Could not redeem quick-invite link.", SteamReasonCodes.Unknown, retryable: true);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SteamFriendsSnapshot> GetFriendsSnapshotAsync(
        string sessionPayload,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            throw new SteamGatewayOperationException(
                parseError,
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        try
        {
            return await ExecuteWithLoggedOnSteamClientAsync(
                bundle,
                async (_, _, _, steamFriends, callbackManager, token) =>
                {
                    var initialListTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    callbackManager.Subscribe<SteamFriends.FriendsListCallback>(callback =>
                    {
                        if (!callback.Incremental)
                        {
                            initialListTcs.TrySetResult(true);
                        }
                    });

                    try
                    {
                        await initialListTcs.Task.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Continue with best-effort data from cache.
                    }

                    var friendIds = new List<SteamID>();
                    var total = steamFriends.GetFriendCount();
                    for (var i = 0; i < total; i++)
                    {
                        var friendId = steamFriends.GetFriendByIndex(i);
                        if (friendId is null || !friendId.IsIndividualAccount)
                        {
                            continue;
                        }

                        var relationship = steamFriends.GetFriendRelationship(friendId);
                        if (relationship != EFriendRelationship.Friend)
                        {
                            continue;
                        }

                        friendIds.Add(friendId);
                    }

                    if (friendIds.Count > 0)
                    {
                        steamFriends.RequestFriendInfo(friendIds, EClientPersonaStateFlag.PlayerName);
                        await Task.Delay(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false);
                    }

                    var friends = friendIds
                        .Select(friendId =>
                        {
                            var sid64 = friendId.ConvertToUInt64().ToString(CultureInfo.InvariantCulture);
                            var personaName = steamFriends.GetFriendPersonaName(friendId);
                            return new SteamFriend
                            {
                                SteamId64 = sid64,
                                PersonaName = string.IsNullOrWhiteSpace(personaName) ? null : personaName,
                                ProfileUrl = $"https://steamcommunity.com/profiles/{sid64}"
                            };
                        })
                        .OrderBy(x => x.PersonaName ?? x.SteamId64, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    return new SteamFriendsSnapshot
                    {
                        SyncedAt = DateTimeOffset.UtcNow,
                        Friends = friends
                    };
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SteamGatewayOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh friends snapshot for steamId={SteamId}", bundle.SteamId64);
            throw ClassifyGatewayException(
                ex,
                "Could not refresh Steam friends list.");
        }
    }

    public async Task<string?> ResolveSteamIdAsync(string loginName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(loginName))
        {
            return null;
        }

        if (ulong.TryParse(loginName, NumberStyles.None, CultureInfo.InvariantCulture, out var asNumber))
        {
            return asNumber.ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://steamcommunity.com/id/{Uri.EscapeDataString(loginName)}?xml=1");
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var match = Regex.Match(body, "<steamID64>(?<id>\\d+)</steamID64>", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["id"].Value : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve SteamId for login '{LoginName}'", loginName);
            return null;
        }
    }
}

