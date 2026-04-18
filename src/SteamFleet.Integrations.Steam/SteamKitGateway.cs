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

public sealed class SteamKitGateway(
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

    public async Task<SteamQrAuthStartResult> StartQrAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        CleanupExpiredQrFlows();

        var steamClient = new SteamClient();
        var manager = new CallbackManager(steamClient);
        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult());
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            if (!connectedTcs.Task.IsCompleted)
            {
                connectedTcs.TrySetException(new InvalidOperationException("Steam CM connection failed."));
            }
        });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.QrFlowTtlSeconds)));

        var callbackPump = Task.Run(() => PumpCallbacksAsync(manager, cts.Token), CancellationToken.None);

        steamClient.Connect();
        await connectedTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            WebsiteID = _options.WebsiteId,
            DeviceFriendlyName = _options.DeviceFriendlyName,
            PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp
        }).ConfigureAwait(false);

        var flowId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, _options.QrFlowTtlSeconds));
        var flow = new QrFlowState(
            flowId,
            expiresAt,
            steamClient,
            authSession,
            cts,
            callbackPump,
            Math.Max(1, (int)Math.Ceiling(authSession.PollingInterval.TotalSeconds)));

        authSession.ChallengeURLChanged = () => flow.UpdateChallengeUrl(authSession.ChallengeURL);
        flow.UpdateChallengeUrl(authSession.ChallengeURL);

        _qrFlows[flowId] = flow;
        flow.StartMonitor(() => MonitorQrFlowAsync(flow));

        logger.LogInformation("Started Steam QR auth flow {FlowId}", flowId);

        return new SteamQrAuthStartResult
        {
            FlowId = flowId,
            ChallengeUrl = flow.ChallengeUrl,
            ExpiresAt = flow.ExpiresAt,
            PollingIntervalSeconds = flow.PollingIntervalSeconds
        };
    }

    public Task<SteamQrAuthPollResult> PollQrAuthenticationAsync(Guid flowId, CancellationToken cancellationToken = default)
    {
        CleanupExpiredQrFlows();

        if (!_qrFlows.TryGetValue(flowId, out var flow))
        {
            return Task.FromResult(new SteamQrAuthPollResult
            {
                FlowId = flowId,
                Status = SteamQrAuthStatus.Expired,
                ErrorMessage = "QR flow not found or expired.",
                ExpiresAt = DateTimeOffset.UtcNow
            });
        }

        return Task.FromResult(flow.GetSnapshot());
    }

    public Task CancelQrAuthenticationAsync(Guid flowId, CancellationToken cancellationToken = default)
    {
        if (_qrFlows.TryGetValue(flowId, out var flow))
        {
            flow.MarkCanceled("QR flow was canceled.");
        }

        CleanupExpiredQrFlows();
        return Task.CompletedTask;
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

        using var webSession = CreateWebSession(bundle);
        await WarmupPasswordFlowAsync(webSession, cancellationToken).ConfigureAwait(false);

        var wizardAttempt = await TryChangePasswordViaSupportWizardAsync(
                bundle,
                currentPassword,
                newPassword,
                confirmationCode,
                confirmationContext,
                webSession,
                cancellationToken)
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

        var storeAttempt = await TryChangePasswordViaStoreAccountAsync(bundle, currentPassword, newPassword, webSession, cancellationToken)
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

        try
        {
            try
            {
                var fromWeb = await TryGetFriendInviteLinkFromWebAsync(bundle, cancellationToken).ConfigureAwait(false);
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
            return Failure("Steam rate limited invite redemption.", SteamReasonCodes.AntiBotBlocked, retryable: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Friend invite redemption failed for steamId={SteamId}", bundle.SteamId64);
            return Failure("Could not redeem quick-invite link.", SteamReasonCodes.Unknown, retryable: true);
        }
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

    private async Task<SteamAuthResult> BuildAuthResultFromQrAsync(AuthPollResult pollResult, CancellationToken cancellationToken)
    {
        var steamId64 = TryGetSteamIdFromJwt(pollResult.AccessToken);
        if (string.IsNullOrWhiteSpace(steamId64))
        {
            steamId64 = await ResolveSteamIdAsync(pollResult.AccountName, cancellationToken).ConfigureAwait(false);
        }

        var bundle = new SteamSessionBundle
        {
            SteamId64 = steamId64 ?? string.Empty,
            AccountName = pollResult.AccountName,
            LoginName = pollResult.AccountName,
            AccessToken = pollResult.AccessToken,
            RefreshToken = pollResult.RefreshToken,
            GuardData = pollResult.NewGuardData,
            SessionId = GenerateSessionId(),
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = TryGetJwtExpiry(pollResult.AccessToken)
        };

        return new SteamAuthResult
        {
            Success = true,
            SteamId64 = bundle.SteamId64,
            AccountName = bundle.AccountName,
            GuardData = bundle.GuardData,
            Session = new SteamSessionInfo
            {
                AccessToken = bundle.AccessToken,
                RefreshToken = bundle.RefreshToken,
                CookiePayload = SerializeBundle(bundle),
                ExpiresAt = bundle.ExpiresAt
            }
        };
    }

    private async Task MonitorQrFlowAsync(QrFlowState flow)
    {
        try
        {
            using var ttlCts = CancellationTokenSource.CreateLinkedTokenSource(flow.Cancellation.Token);
            ttlCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, _options.QrFlowTtlSeconds)));

            var pollResult = await flow.AuthSession.PollingWaitForResultAsync(ttlCts.Token).ConfigureAwait(false);
            var authResult = await BuildAuthResultFromQrAsync(pollResult, ttlCts.Token).ConfigureAwait(false);

            flow.MarkCompleted(authResult);
            logger.LogInformation("Steam QR auth flow {FlowId} completed", flow.FlowId);
        }
        catch (OperationCanceledException)
        {
            if (flow.IsCanceled)
            {
                flow.MarkCanceled("QR flow was canceled.");
            }
            else
            {
                flow.MarkExpired("QR flow timed out.");
            }
        }
        catch (AuthenticationException ex)
        {
            flow.MarkFailed(MapAuthenticationError(ex.Result));
            logger.LogWarning("Steam QR auth flow {FlowId} failed: {Result}", flow.FlowId, ex.Result);
        }
        catch (Exception ex)
        {
            flow.MarkFailed("QR authentication failed.");
            logger.LogWarning(ex, "Steam QR auth flow {FlowId} failed with exception", flow.FlowId);
        }
        finally
        {
            await flow.StopRuntimeAsync().ConfigureAwait(false);
        }
    }

    private void CleanupExpiredQrFlows()
    {
        var utcNow = DateTimeOffset.UtcNow;
        foreach (var (flowId, flow) in _qrFlows.ToArray())
        {
            if (flow.ExpiresAt > utcNow)
            {
                continue;
            }

            flow.MarkExpired("QR flow expired.");
            _qrFlows.TryRemove(flowId, out _);
            _ = flow.StopRuntimeAsync();
        }
    }

    private async Task<T> ExecuteWithSteamClientAsync<T>(
        Func<SteamClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var steamClient = new SteamClient();
        var callbackManager = new CallbackManager(steamClient);
        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        callbackManager.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult());
        callbackManager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            if (!connectedTcs.Task.IsCompleted)
            {
                connectedTcs.TrySetException(new InvalidOperationException("Steam CM connection failed."));
            }
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.AuthTimeoutSeconds)));

        var callbackPump = Task.Run(() => PumpCallbacksAsync(callbackManager, timeoutCts.Token), CancellationToken.None);

        steamClient.Connect();

        try
        {
            await connectedTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return await operation(steamClient, timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                steamClient.Disconnect();
            }
            catch
            {
                // ignored
            }

            timeoutCts.Cancel();

            try
            {
                await callbackPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        }
    }

    private async Task<T> ExecuteWithLoggedOnSteamClientAsync<T>(
        SteamSessionBundle bundle,
        Func<SteamClient, SteamUser, SteamUnifiedMessages, SteamFriends, CallbackManager, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var candidates = await BuildLogOnTokenCandidatesAsync(bundle, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new SteamGatewayOperationException(
                "Session payload does not contain Steam auth token.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        SteamGatewayOperationException? lastGatewayFailure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var hasNext = i + 1 < candidates.Count;

            try
            {
                return await ExecuteWithLoggedOnSteamClientAttemptAsync(
                        bundle,
                        candidate.Token,
                        operation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SteamGatewayOperationException ex) when (hasNext && ShouldFallbackToNextLogOnToken(ex))
            {
                logger.LogInformation(
                    "Steam CM logon token candidate failed. steamId={SteamId} tokenKind={TokenKind} reason={ReasonCode} retryable={Retryable}; trying next token.",
                    bundle.SteamId64,
                    candidate.Kind,
                    ex.ReasonCode,
                    ex.Retryable);
                lastGatewayFailure = ex;
            }
        }

        if (lastGatewayFailure is not null)
        {
            throw lastGatewayFailure;
        }

        throw new SteamGatewayOperationException(
            "Steam CM logon failed for all token candidates.",
            SteamReasonCodes.AuthSessionMissing,
            retryable: true);
    }

    private async Task<IReadOnlyList<(string Token, string Kind)>> BuildLogOnTokenCandidatesAsync(
        SteamSessionBundle bundle,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Token, string Kind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(string? token, string kind)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var normalized = token.Trim();
            if (seen.Add(normalized))
            {
                candidates.Add((normalized, kind));
            }
        }

        AddCandidate(bundle.RefreshToken, "refresh");
        AddCandidate(bundle.AccessToken, "access");

        var generated = await TryGenerateClientAccessTokenAsync(bundle, cancellationToken).ConfigureAwait(false);
        if (generated is not null)
        {
            if (!string.IsNullOrWhiteSpace(generated.RefreshToken))
            {
                bundle.RefreshToken = generated.RefreshToken;
            }

            if (!string.IsNullOrWhiteSpace(generated.AccessToken))
            {
                bundle.AccessToken = generated.AccessToken;
            }

            AddCandidate(generated.RefreshToken, "generated_refresh");
            AddCandidate(generated.AccessToken, "generated_access");
        }

        return candidates;
    }

    private async Task<AccessTokenGenerateResult?> TryGenerateClientAccessTokenAsync(
        SteamSessionBundle bundle,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bundle.RefreshToken) ||
            !ulong.TryParse(bundle.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var steamIdRaw))
        {
            return null;
        }

        try
        {
            return await ExecuteWithSteamClientAsync(
                    async (steamClient, _) =>
                    {
                        return await steamClient.Authentication.GenerateAccessTokenForAppAsync(
                                new SteamID(steamIdRaw),
                                bundle.RefreshToken,
                                allowRenewal: true)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to generate Steam CM access token candidate from refresh token. steamId={SteamId}",
                bundle.SteamId64);
            return null;
        }
    }

    private static bool ShouldFallbackToNextLogOnToken(SteamGatewayOperationException ex)
    {
        if (!ex.Retryable)
        {
            return false;
        }

        return string.Equals(ex.ReasonCode, SteamReasonCodes.AuthSessionMissing, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ex.ReasonCode, SteamReasonCodes.Timeout, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ex.ReasonCode, SteamReasonCodes.Unknown, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<T> ExecuteWithLoggedOnSteamClientAttemptAsync<T>(
        SteamSessionBundle bundle,
        string token,
        Func<SteamClient, SteamUser, SteamUnifiedMessages, SteamFriends, CallbackManager, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var steamClient = new SteamClient();
        var callbackManager = new CallbackManager(steamClient);
        var steamUser = steamClient.GetHandler<SteamUser>() ?? throw new InvalidOperationException("SteamUser handler is unavailable.");
        var steamFriends = steamClient.GetHandler<SteamFriends>() ?? throw new InvalidOperationException("SteamFriends handler is unavailable.");
        var unifiedMessages = steamClient.GetHandler<SteamUnifiedMessages>() ?? throw new InvalidOperationException("SteamUnifiedMessages handler is unavailable.");

        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggedOnTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        callbackManager.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult());
        callbackManager.Subscribe<SteamClient.DisconnectedCallback>(callback =>
        {
            var reason = callback.UserInitiated ? "disconnected by client" : "connection dropped";
            if (!connectedTcs.Task.IsCompleted)
            {
                connectedTcs.TrySetException(new SteamGatewayOperationException(
                    $"Steam CM connection failed: {reason}.",
                    SteamReasonCodes.Timeout,
                    retryable: true));
            }

            if (!loggedOnTcs.Task.IsCompleted)
            {
                loggedOnTcs.TrySetException(new SteamGatewayOperationException(
                    $"Steam CM disconnected before logon: {reason}.",
                    SteamReasonCodes.Timeout,
                    retryable: true));
            }
        });

        callbackManager.Subscribe<SteamUser.LoggedOnCallback>(callback =>
        {
            if (callback.Result == EResult.OK)
            {
                loggedOnTcs.TrySetResult();
                return;
            }

            loggedOnTcs.TrySetException(CreateLogonFailureException(callback.Result, callback.ExtendedResult));
        });

        callbackManager.Subscribe<SteamUser.LoggedOffCallback>(callback =>
        {
            if (!loggedOnTcs.Task.IsCompleted)
            {
                loggedOnTcs.TrySetException(new SteamGatewayOperationException(
                    $"Steam logged off before operation: {callback.Result}.",
                    MapLogonFailureReason(callback.Result, EResult.Invalid, out var retryable),
                    retryable));
            }
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.AuthTimeoutSeconds)));

        var callbackPump = Task.Run(() => PumpCallbacksAsync(callbackManager, timeoutCts.Token), CancellationToken.None);

        steamClient.Connect();

        try
        {
            await connectedTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new SteamGatewayOperationException(
                    "Session payload does not contain Steam auth token.",
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true);
            }

            uint? accountId = null;
            if (ulong.TryParse(bundle.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId64))
            {
                accountId = new SteamID(steamId64).AccountID;
            }

            var username = ResolveLogOnUsername(bundle);
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new SteamGatewayOperationException(
                    "Session payload does not contain Steam account login.",
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true);
            }

            var details = new SteamUser.LogOnDetails
            {
                Username = username,
                AccessToken = token,
                ShouldRememberPassword = true,
                MachineName = _options.DeviceFriendlyName,
                ClientLanguage = "english"
            };

            if (accountId is not null)
            {
                details.AccountID = accountId.Value;
            }

            steamUser.LogOn(details);
            await loggedOnTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

            return await operation(steamClient, steamUser, unifiedMessages, steamFriends, callbackManager, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new SteamGatewayOperationException(
                "Steam CM operation timed out.",
                SteamReasonCodes.Timeout,
                retryable: true,
                ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SteamGatewayOperationException(
                "Steam CM operation timed out.",
                SteamReasonCodes.Timeout,
                retryable: true,
                ex);
        }
        finally
        {
            try
            {
                steamUser.LogOff();
            }
            catch
            {
                // ignored
            }

            try
            {
                steamClient.Disconnect();
            }
            catch
            {
                // ignored
            }

            timeoutCts.Cancel();

            try
            {
                await callbackPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        }
    }

    private static async Task PumpCallbacksAsync(CallbackManager manager, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
            await Task.Yield();
        }
    }

    private async Task<PagePathResult> TryGetProfilePathAsync(SteamSessionBundle bundle, CancellationToken cancellationToken)
    {
        using var response = await SendCommunityRequestAsync(
            HttpMethod.Get,
            "https://steamcommunity.com/my",
            bundle,
            content: null,
            allowRedirects: false,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return PagePathResult.Fail("Unauthorized session.");
        }

        if (IsRedirectStatusCode(response.StatusCode))
        {
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            if (location.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                return PagePathResult.Fail("Session is not authorized.");
            }

            var match = ProfilePathRegex.Match(location);
            if (match.Success)
            {
                return PagePathResult.FromPath(match.Groups["path"].Value);
            }
        }

        if (response.IsSuccessStatusCode)
        {
            return PagePathResult.FromPath("/my");
        }

        return PagePathResult.Fail($"Steam community responded {(int)response.StatusCode}.");
    }

    private async Task<string> GetProfilePathOrThrowAsync(SteamSessionBundle bundle, CancellationToken cancellationToken)
    {
        var result = await TryGetProfilePathAsync(bundle, cancellationToken).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Path))
        {
            throw new SteamGatewayOperationException(
                result.ErrorMessage ?? "Cannot resolve Steam profile path.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        return result.Path;
    }

    private async Task<string> GetPageHtmlAsync(string url, SteamSessionBundle bundle, CancellationToken cancellationToken)
    {
        using var response = await SendCommunityRequestAsync(
            HttpMethod.Get,
            url,
            bundle,
            content: null,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SteamGatewayOperationException(
                $"Steam endpoint {url} returned {(int)response.StatusCode}.",
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? SteamReasonCodes.AuthSessionMissing
                    : SteamReasonCodes.Unknown,
                retryable: response.StatusCode == HttpStatusCode.Unauthorized);
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var effectiveUri = response.RequestMessage?.RequestUri;

        if (LooksLikeCommunityLoginPage(html) ||
            (effectiveUri is not null &&
             effectiveUri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SteamGatewayOperationException(
                "Steam community session is not authorized for games page.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        return html;
    }

    private async Task<SteamOperationResult> TryChangePasswordViaSupportWizardAsync(
        SteamSessionBundle bundle,
        string currentPassword,
        string newPassword,
        string? confirmationCode,
        string? confirmationContext,
        SteamWebSession webSession,
        CancellationToken cancellationToken)
    {
        try
        {
            var hasConfirmationCode = !string.IsNullOrWhiteSpace(confirmationCode);
            var html = await EnsureHelpSessionAuthenticatedAsync(bundle, webSession, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                return Failure(
                    "Steam Support wizard requires authenticated help session.",
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true);
            }

            var sessionId = ExtractSessionId(html) ?? bundle.SessionId;
            var wizardUri = webSession.LastUri ?? new Uri("https://help.steampowered.com/en/wizard/HelpChangePassword");
            var confirmationUri = NormalizeHelpWizardUri(confirmationContext);
            if (confirmationUri is not null)
            {
                html = await GetHelpPageHtmlAsync(confirmationUri.ToString(), bundle, webSession, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                {
                    return Failure(
                        "Steam Support wizard requires authenticated help session.",
                        SteamReasonCodes.AuthSessionMissing,
                        retryable: true);
                }

                wizardUri = webSession.LastUri ?? confirmationUri;
                sessionId = ExtractSessionId(html) ?? bundle.SessionId;
            }

            if (TryResolvePasswordRecoveryContext(html, wizardUri, confirmationContext, out var recoveryContext))
            {
                if (!hasConfirmationCode)
                {
                    var sendRecoveryCodeAttempt = await TrySendPasswordRecoveryCodeAsync(
                            bundle,
                            webSession,
                            sessionId,
                            recoveryContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return sendRecoveryCodeAttempt;
                }
                else
                {
                    var completeRecoveryAttempt = await TryCompletePasswordChangeWithRecoveryCodeAsync(
                            bundle,
                            webSession,
                            sessionId,
                            newPassword,
                            confirmationCode!,
                            recoveryContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return completeRecoveryAttempt;
                }
            }

            var context = TryGetQueryParameter(wizardUri, "context");
            var pageHidden = ExtractHiddenInputs(html);

            var formCandidates = ExtractFormCandidates(
                html,
                "https://help.steampowered.com",
                form => form.Action.Contains("/wizard/", StringComparison.OrdinalIgnoreCase) &&
                        (form.Action.Contains("Ajax", StringComparison.OrdinalIgnoreCase) ||
                         form.Action.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                         form.Action.Contains("verify", StringComparison.OrdinalIgnoreCase)));

            var endpoints = ExtractWizardEndpoints(html, wizardUri, formCandidates);
            if (hasConfirmationCode)
            {
                endpoints = endpoints
                    .OrderByDescending(x => x.Contains("EnterCode", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.Contains("verify", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.Contains("changepassword", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            foreach (var endpoint in endpoints)
            {
                var formData = new Dictionary<string, string>(pageHidden, StringComparer.Ordinal);
                var formSpec = formCandidates.FirstOrDefault(x => string.Equals(x.Action, endpoint, StringComparison.OrdinalIgnoreCase));
                if (formSpec is not null)
                {
                    foreach (var (key, value) in formSpec.HiddenFields)
                    {
                        formData[key] = value;
                    }
                }

                formData["sessionid"] = sessionId;
                formData["wizard_ajax"] = "1";
                formData["oldpassword"] = currentPassword;
                formData["old_password"] = currentPassword;
                formData["currentpassword"] = currentPassword;
                formData["newpassword"] = newPassword;
                formData["new_password"] = newPassword;
                formData["reenternewpassword"] = newPassword;
                formData["newpassword_confirm"] = newPassword;
                formData["confirm_password"] = newPassword;
                if (hasConfirmationCode)
                {
                    formData["code"] = confirmationCode!;
                    formData["authcode"] = confirmationCode!;
                    formData["guardcode"] = confirmationCode!;
                    formData["guard_code"] = confirmationCode!;
                    formData["emailauth"] = confirmationCode!;
                    formData["email_auth"] = confirmationCode!;
                    formData["verification_code"] = confirmationCode!;
                    formData["confirmcode"] = confirmationCode!;
                    formData["confirm_code"] = confirmationCode!;
                }

                if (!string.IsNullOrWhiteSpace(context) && !formData.ContainsKey("context"))
                {
                    formData["context"] = context;
                }

                var endpointMethod =
                    endpoint.Contains("HelpWithLoginInfoSendCode", StringComparison.OrdinalIgnoreCase) && !hasConfirmationCode
                        ? HttpMethod.Get
                        : HttpMethod.Post;

                using var response = endpointMethod == HttpMethod.Get
                    ? await SendHelpRequestAsync(
                            HttpMethod.Get,
                            endpoint,
                            bundle,
                            webSession,
                            content: null,
                            allowRedirects: true,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await SendHelpFormAsync(
                            HttpMethod.Post,
                            endpoint,
                            bundle,
                            webSession,
                            formData,
                            allowRedirects: true,
                            cancellationToken)
                        .ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var responseUri = response.RequestMessage?.RequestUri;
                var responsePath = responseUri?.AbsolutePath ?? string.Empty;

                logger.LogInformation(
                    "Steam password flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
                    "support_wizard",
                    responseUri?.Host,
                    responsePath,
                    (int)response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                        response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return Failure("Steam support flow is temporarily blocked.", SteamReasonCodes.AntiBotBlocked, retryable: true);
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized ||
                        responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure("Steam support session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
                    }

                    if (response.StatusCode == HttpStatusCode.MethodNotAllowed ||
                        response.StatusCode == HttpStatusCode.NotFound)
                    {
                        logger.LogInformation(
                            "Steam password flow candidate rejected. flow={Flow} reason={Reason} host={Host} path={Path}",
                            "support_wizard",
                            SteamReasonCodes.EndpointRejected,
                            responseUri?.Host,
                            responsePath);
                        continue;
                    }

                    continue;
                }

                if (responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase) || LooksLikeHelpLoginPage(body))
                {
                    return Failure("Steam support session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
                }

                if (responsePath.Contains("HelpWithLoginInfoEnterCode", StringComparison.OrdinalIgnoreCase) ||
                    responsePath.Contains("HelpWithLoginInfoSendCode", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        hasConfirmationCode
                            ? "Код подтверждения пока не принят Steam. Проверьте код из письма и повторите попытку."
                            : "Steam запросил email-подтверждение смены пароля. Проверьте почту аккаунта и повторите операцию.",
                        SteamReasonCodes.GuardPending,
                        retryable: true,
                        data: BuildPasswordConfirmationData(responseUri?.ToString() ?? endpoint));
                }

                if (TryParseSuccessFromJson(body, out var wizardErrorText) ||
                    body.Contains("password has been changed", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("password was changed", StringComparison.OrdinalIgnoreCase))
                {
                    return new SteamOperationResult
                    {
                        Success = true,
                        ReasonCode = SteamReasonCodes.None,
                        Data = new Dictionary<string, string> { ["flow"] = "support_wizard" }
                    };
                }

                if (LooksLikeGuardPending(body))
                {
                    return Failure(
                        hasConfirmationCode
                            ? "Steam всё ещё ожидает подтверждение из письма."
                            : "Steam Guard confirmation is pending.",
                        SteamReasonCodes.GuardPending,
                        retryable: true,
                        data: BuildPasswordConfirmationData(responseUri?.ToString() ?? endpoint));
                }

                if (LooksLikeAntiBot(body))
                {
                    return Failure("Steam anti-bot blocked the support flow.", SteamReasonCodes.AntiBotBlocked, retryable: true);
                }

                if (!string.IsNullOrWhiteSpace(wizardErrorText))
                {
                    if (wizardErrorText.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                        wizardErrorText.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
                        wizardErrorText.Contains("authorize", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure(wizardErrorText, SteamReasonCodes.AuthSessionMissing, retryable: true);
                    }

                    return Failure(wizardErrorText, SteamReasonCodes.EndpointRejected);
                }
            }

            if (LooksLikeGuardPending(html))
            {
                return Failure(
                    "Steam запросил email-подтверждение смены пароля. Проверьте почту аккаунта и повторите операцию.",
                    SteamReasonCodes.GuardPending,
                    retryable: true,
                    data: BuildPasswordConfirmationData(wizardUri.ToString()));
            }

            if (hasConfirmationCode)
            {
                return Failure(
                    "Steam не принял код подтверждения для смены пароля.",
                    SteamReasonCodes.GuardPending,
                    retryable: true,
                    data: BuildPasswordConfirmationData(wizardUri.ToString()));
            }

            return Failure(
                "Steam Support wizard did not expose an accepted password-change endpoint.",
                SteamReasonCodes.EndpointRejected);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Support wizard password change flow timed out");
            return Failure("Support wizard flow timed out.", SteamReasonCodes.Timeout, retryable: true);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Support wizard password change flow canceled");
            return Failure("Support wizard flow timed out.", SteamReasonCodes.Timeout, retryable: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Support wizard password change flow failed");
            return Failure("Support wizard flow failed.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    private async Task<SteamOperationResult> TryChangePasswordViaStoreAccountAsync(
        SteamSessionBundle bundle,
        string currentPassword,
        string newPassword,
        SteamWebSession webSession,
        CancellationToken cancellationToken)
    {
        try
        {
            const string targetUrl = "https://store.steampowered.com/account/changepassword";
            var html = await GetStorePageHtmlAsync(targetUrl, bundle, webSession, cancellationToken).ConfigureAwait(false);
            if (webSession.LastUri is { } storeUri &&
                storeUri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("Store session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
            }

            var sessionId = ExtractSessionId(html) ?? bundle.SessionId;
            var pageHidden = ExtractHiddenInputs(html);
            var candidates = ExtractStorePasswordCandidates(html);
            if (candidates.Count == 0)
            {
                return Failure(
                    "Store did not expose password change submit endpoints.",
                    SteamReasonCodes.EndpointRejected);
            }

            foreach (var candidate in candidates)
            {
                if (!candidate.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var actionUrl = candidate.Action;
                if (!IsAcceptedStorePasswordSubmitEndpoint(actionUrl))
                {
                    continue;
                }

                var form = new Dictionary<string, string>(pageHidden, StringComparer.Ordinal);
                foreach (var (key, value) in candidate.HiddenFields)
                {
                    form[key] = value;
                }

                form["sessionid"] = sessionId;
                form["sessionID"] = sessionId;
                form["oldpassword"] = currentPassword;
                form["old_password"] = currentPassword;
                form["currentpassword"] = currentPassword;
                form["newpassword"] = newPassword;
                form["new_password"] = newPassword;
                form["reenternewpassword"] = newPassword;
                form["confirm_password"] = newPassword;
                form["verify_password"] = newPassword;
                form["newpassword_confirm"] = newPassword;

                using var response = await SendStoreFormAsync(
                    HttpMethod.Post,
                    actionUrl,
                    bundle,
                    webSession,
                    form,
                    allowRedirects: true,
                    cancellationToken).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var responseUri = response.RequestMessage?.RequestUri;
                var responsePath = responseUri?.AbsolutePath ?? string.Empty;

                logger.LogInformation(
                    "Steam password flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
                    "store",
                    responseUri?.Host,
                    responsePath,
                    (int)response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                        response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return Failure($"Store password change failed: HTTP {(int)response.StatusCode}", SteamReasonCodes.AntiBotBlocked, retryable: true);
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized ||
                        responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure($"Store password change failed: HTTP {(int)response.StatusCode}", SteamReasonCodes.AuthSessionMissing, retryable: true);
                    }

                    if (response.StatusCode == HttpStatusCode.MethodNotAllowed ||
                        response.StatusCode == HttpStatusCode.NotFound)
                    {
                        logger.LogInformation(
                            "Steam password flow candidate rejected. flow={Flow} reason={Reason} host={Host} path={Path}",
                            "store",
                            SteamReasonCodes.EndpointRejected,
                            responseUri?.Host,
                            responsePath);
                        continue;
                    }

                    continue;
                }

                if (responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure("Store session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
                }

                if (TryParseSuccessFromJson(body, out var errorText))
                {
                    return new SteamOperationResult
                    {
                        Success = true,
                        ReasonCode = SteamReasonCodes.None,
                        Data = new Dictionary<string, string> { ["flow"] = "store_account" }
                    };
                }

                if (LooksLikeGuardPending(body))
                {
                    return Failure("Steam Guard confirmation is pending.", SteamReasonCodes.GuardPending, retryable: true);
                }

                if (LooksLikeAntiBot(body))
                {
                    return Failure("Steam anti-bot blocked the store flow.", SteamReasonCodes.AntiBotBlocked, retryable: true);
                }

                if (body.Contains("password has been changed", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("password was changed", StringComparison.OrdinalIgnoreCase))
                {
                    return new SteamOperationResult
                    {
                        Success = true,
                        ReasonCode = SteamReasonCodes.None,
                        Data = new Dictionary<string, string> { ["flow"] = "store_account_html" }
                    };
                }

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    if (errorText.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                        errorText.Contains("authorize", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure(errorText, SteamReasonCodes.AuthSessionMissing, retryable: true);
                    }

                    return Failure(errorText, SteamReasonCodes.EndpointRejected);
                }
            }

            return Failure(
                "Store password change did not expose an accepted submit endpoint.",
                SteamReasonCodes.EndpointRejected);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Store password change flow timed out");
            return Failure("Store account flow timed out.", SteamReasonCodes.Timeout, retryable: true);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Store password change flow canceled");
            return Failure("Store account flow timed out.", SteamReasonCodes.Timeout, retryable: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Store account password change flow failed");
            return Failure("Store account flow failed.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    private static SteamOperationResult Failure(
        string message,
        string? reasonCode = null,
        bool retryable = false,
        IReadOnlyDictionary<string, string>? data = null)
    {
        return new SteamOperationResult
        {
            Success = false,
            ErrorMessage = message,
            ReasonCode = reasonCode ?? SteamReasonCodes.Unknown,
            Retryable = retryable,
            Data = data is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static Dictionary<string, string> BuildPasswordConfirmationData(string? confirmationContext)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["confirmationRequired"] = true.ToString()
        };

        if (!string.IsNullOrWhiteSpace(confirmationContext))
        {
            payload["confirmationContext"] = confirmationContext.Trim();
        }

        return payload;
    }

    private static Uri? NormalizeHelpWizardUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var absolute) &&
            absolute.Host.Equals("help.steampowered.com", StringComparison.OrdinalIgnoreCase))
        {
            return absolute;
        }

        if (Uri.TryCreate(raw.Trim(), UriKind.Relative, out var relative))
        {
            var baseUri = new Uri("https://help.steampowered.com", UriKind.Absolute);
            var merged = new Uri(baseUri, relative);
            return merged.Host.Equals("help.steampowered.com", StringComparison.OrdinalIgnoreCase) ? merged : null;
        }

        return null;
    }

    private async Task<SteamOperationResult> TrySendPasswordRecoveryCodeAsync(
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        string sessionId,
        PasswordRecoveryContext context,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionid"] = sessionId,
            ["wizard_ajax"] = "1",
            ["s"] = context.SessionToken,
            ["method"] = context.Method,
            ["link"] = string.Empty,
            ["n"] = "1"
        };

        using var response = await SendHelpFormAsync(
                HttpMethod.Post,
                "https://help.steampowered.com/en/wizard/AjaxSendAccountRecoveryCode",
                bundle,
                webSession,
                form,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var responseUri = response.RequestMessage?.RequestUri;
        logger.LogInformation(
            "Steam password flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
            "support_wizard_send_code",
            responseUri?.Host,
            responseUri?.AbsolutePath,
            (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return Failure(
                    "Steam временно заблокировал отправку кода подтверждения.",
                    SteamReasonCodes.AntiBotBlocked,
                    retryable: true);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Failure(
                    "Steam support session is unauthorized.",
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true);
            }

            return Failure(
                $"Steam send-code endpoint returned HTTP {(int)response.StatusCode}.",
                SteamReasonCodes.EndpointRejected);
        }

        if (TryParseWizardAjaxResponse(body, out var success, out var hash, out var html, out var errorMessage))
        {
            if (success || !string.IsNullOrWhiteSpace(hash) || !string.IsNullOrWhiteSpace(html))
            {
                return Failure(
                    "Steam отправил код подтверждения на почту. Введите код из письма, чтобы завершить смену пароля.",
                    SteamReasonCodes.GuardPending,
                    retryable: true,
                    data: BuildPasswordConfirmationData(context.EnterCodeUrl));
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                var reason = ClassifyPasswordWizardError(errorMessage, out var retryable);
                return Failure(errorMessage, reason, retryable, BuildPasswordConfirmationData(context.EnterCodeUrl));
            }
        }

        if (LooksLikeAntiBot(body))
        {
            return Failure("Steam anti-bot blocked the support flow.", SteamReasonCodes.AntiBotBlocked, retryable: true);
        }

        if (LooksLikeHelpLoginPage(body))
        {
            return Failure("Steam support session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        return Failure(
            "Steam не подтвердил отправку кода восстановления.",
            SteamReasonCodes.EndpointRejected,
            data: BuildPasswordConfirmationData(context.EnterCodeUrl));
    }

    private async Task<SteamOperationResult> TryCompletePasswordChangeWithRecoveryCodeAsync(
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        string sessionId,
        string newPassword,
        string confirmationCode,
        PasswordRecoveryContext context,
        CancellationToken cancellationToken)
    {
        var enterCodeUrl = context.EnterCodeUrl;
        if (string.IsNullOrWhiteSpace(enterCodeUrl))
        {
            return Failure(
                "Не удалось определить страницу подтверждения кода Steam.",
                SteamReasonCodes.EndpointRejected);
        }

        var enterCodeHtml = await GetHelpPageHtmlAsync(enterCodeUrl, bundle, webSession, cancellationToken).ConfigureAwait(false);
        if (LooksLikeHelpLoginPage(enterCodeHtml))
        {
            return Failure("Steam support session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        var enterHidden = ExtractHiddenInputs(enterCodeHtml);
        var verifyForm = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionid"] = sessionId,
            ["wizard_ajax"] = "1",
            ["code"] = confirmationCode.Trim(),
            ["s"] = context.SessionToken,
            ["reset"] = context.Reset,
            ["lost"] = context.Lost,
            ["method"] = string.IsNullOrWhiteSpace(context.Method) ? "2" : context.Method
        };
        if (!string.IsNullOrWhiteSpace(context.IssueId))
        {
            verifyForm["issueid"] = context.IssueId;
        }

        foreach (var (key, value) in enterHidden)
        {
            if (!verifyForm.ContainsKey(key))
            {
                verifyForm[key] = value;
            }
        }

        using var verifyResponse = await SendHelpFormAsync(
                HttpMethod.Post,
                "https://help.steampowered.com/en/wizard/AjaxVerifyAccountRecoveryCode/",
                bundle,
                webSession,
                verifyForm,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        var verifyBody = await verifyResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Steam password flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
            "support_wizard_verify_code",
            verifyResponse.RequestMessage?.RequestUri?.Host,
            verifyResponse.RequestMessage?.RequestUri?.AbsolutePath,
            (int)verifyResponse.StatusCode);

        if (!verifyResponse.IsSuccessStatusCode)
        {
            if (verifyResponse.StatusCode == HttpStatusCode.TooManyRequests || verifyResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return Failure("Steam support flow is temporarily blocked.", SteamReasonCodes.AntiBotBlocked, retryable: true);
            }

            if (verifyResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Failure("Steam support session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
            }

            return Failure(
                $"Steam code verification endpoint returned HTTP {(int)verifyResponse.StatusCode}.",
                SteamReasonCodes.EndpointRejected);
        }

        if (!TryParseWizardAjaxResponse(verifyBody, out var verifySuccess, out var verifyHash, out var verifyHtml, out var verifyError))
        {
            return Failure(
                "Steam verification endpoint returned malformed response.",
                SteamReasonCodes.EndpointRejected,
                data: BuildPasswordConfirmationData(context.EnterCodeUrl));
        }

        if (!string.IsNullOrWhiteSpace(verifyError) && string.IsNullOrWhiteSpace(verifyHash) && string.IsNullOrWhiteSpace(verifyHtml))
        {
            var reason = ClassifyPasswordWizardError(verifyError, out var retryable);
            return Failure(verifyError, reason, retryable, BuildPasswordConfirmationData(context.EnterCodeUrl));
        }

        string passwordPageHtml;
        if (!string.IsNullOrWhiteSpace(verifyHash))
        {
            var hashUrl = BuildHelpHashUrl(verifyHash);
            passwordPageHtml = await GetHelpPageHtmlAsync(hashUrl, bundle, webSession, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(verifyHtml))
        {
            passwordPageHtml = verifyHtml;
        }
        else if (verifySuccess)
        {
            passwordPageHtml = await GetHelpPageHtmlAsync(enterCodeUrl, bundle, webSession, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return Failure(
                "Steam did not return a password change page after code verification.",
                SteamReasonCodes.EndpointRejected,
                data: BuildPasswordConfirmationData(context.EnterCodeUrl));
        }

        if (LooksLikeHelpLoginPage(passwordPageHtml))
        {
            return Failure("Steam support session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (!TryExtractPasswordChangeSubmitContext(passwordPageHtml, context, bundle, out var submitContext))
        {
            return Failure(
                "Steam support wizard did not expose password-change submit context.",
                SteamReasonCodes.EndpointRejected,
                data: BuildPasswordConfirmationData(context.EnterCodeUrl));
        }

        var rsa = await RequestHelpRsaKeyAsync(
                bundle,
                webSession,
                sessionId,
                submitContext.LoginName,
                cancellationToken)
            .ConfigureAwait(false);
        if (rsa is null)
        {
            return Failure(
                "Steam did not return RSA key for password encryption.",
                SteamReasonCodes.EndpointRejected);
        }

        var encryptedPassword = EncryptPasswordWithRsa(newPassword, rsa.ModulusHex, rsa.ExponentHex);
        if (string.IsNullOrWhiteSpace(encryptedPassword))
        {
            return Failure(
                "Could not encrypt password for Steam support endpoint.",
                SteamReasonCodes.EndpointRejected);
        }

        var changeForm = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionid"] = sessionId,
            ["wizard_ajax"] = "1",
            ["s"] = submitContext.SessionToken,
            ["account"] = submitContext.AccountId,
            ["password"] = encryptedPassword,
            ["rsatimestamp"] = rsa.Timestamp
        };

        using var changeResponse = await SendHelpFormAsync(
                HttpMethod.Post,
                "https://help.steampowered.com/en/wizard/AjaxAccountRecoveryChangePassword/",
                bundle,
                webSession,
                changeForm,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        var changeBody = await changeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Steam password flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
            "support_wizard_change_password",
            changeResponse.RequestMessage?.RequestUri?.Host,
            changeResponse.RequestMessage?.RequestUri?.AbsolutePath,
            (int)changeResponse.StatusCode);

        if (!changeResponse.IsSuccessStatusCode)
        {
            if (changeResponse.StatusCode == HttpStatusCode.TooManyRequests || changeResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return Failure("Steam support flow is temporarily blocked.", SteamReasonCodes.AntiBotBlocked, retryable: true);
            }

            if (changeResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Failure("Steam support session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
            }

            return Failure(
                $"Steam password change endpoint returned HTTP {(int)changeResponse.StatusCode}.",
                SteamReasonCodes.EndpointRejected);
        }

        if (TryParseWizardAjaxResponse(changeBody, out var changeSuccess, out var changeHash, out var changeHtml, out var changeError))
        {
            if (changeSuccess || !string.IsNullOrWhiteSpace(changeHash) || LooksLikePasswordChangedHtml(changeHtml))
            {
                return new SteamOperationResult
                {
                    Success = true,
                    ReasonCode = SteamReasonCodes.None,
                    Data = new Dictionary<string, string>
                    {
                        ["flow"] = "support_wizard"
                    }
                };
            }

            if (!string.IsNullOrWhiteSpace(changeError))
            {
                var reason = ClassifyPasswordWizardError(changeError, out var retryable);
                return Failure(changeError, reason, retryable, BuildPasswordConfirmationData(context.EnterCodeUrl));
            }
        }

        if (LooksLikePasswordChangedHtml(changeBody))
        {
            return new SteamOperationResult
            {
                Success = true,
                ReasonCode = SteamReasonCodes.None,
                Data = new Dictionary<string, string>
                {
                    ["flow"] = "support_wizard_html"
                }
            };
        }

        return Failure(
            "Steam не подтвердил успешную смену пароля после проверки кода.",
            SteamReasonCodes.EndpointRejected,
            data: BuildPasswordConfirmationData(context.EnterCodeUrl));
    }

    private static bool TryResolvePasswordRecoveryContext(
        string html,
        Uri wizardUri,
        string? confirmationContext,
        out PasswordRecoveryContext context)
    {
        context = default!;
        Uri? enterCodeUri = null;

        if (NormalizeHelpWizardUri(confirmationContext) is { } provided)
        {
            enterCodeUri = provided;
        }
        else if (!string.IsNullOrWhiteSpace(ExtractPasswordEmailConfirmationEndpoint(html, wizardUri)))
        {
            enterCodeUri = NormalizeHelpWizardUri(ExtractPasswordEmailConfirmationEndpoint(html, wizardUri));
        }

        if (enterCodeUri is null)
        {
            return false;
        }

        var s = TryGetQueryParameter(enterCodeUri, "s");
        var account = TryGetQueryParameter(enterCodeUri, "account");
        var reset = TryGetQueryParameter(enterCodeUri, "reset") ?? "1";
        var lost = TryGetQueryParameter(enterCodeUri, "lost") ?? "0";
        var issueId = TryGetQueryParameter(enterCodeUri, "issueid") ?? string.Empty;
        var method = TryGetQueryParameter(enterCodeUri, "method") ?? "2";

        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(account))
        {
            account = TryGetQueryParameter(wizardUri, "account") ?? string.Empty;
        }

        context = new PasswordRecoveryContext(
            SessionToken: s,
            AccountId: account,
            Reset: reset,
            Lost: lost,
            IssueId: issueId,
            Method: method,
            EnterCodeUrl: enterCodeUri.ToString());
        return true;
    }

    private static bool TryParseWizardAjaxResponse(
        string body,
        out bool success,
        out string? hash,
        out string? html,
        out string? errorMessage)
    {
        success = false;
        hash = null;
        html = null;
        errorMessage = null;

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            success = TryReadSuccessFlag(root);
            hash = GetJsonString(root, "hash");
            html = GetJsonString(root, "html");
            errorMessage = GetJsonString(root, "errorMsg") ??
                           GetJsonString(root, "errormsg") ??
                           GetJsonString(root, "message");

            if (!success && root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
            {
                success = TryReadSuccessFlag(response);
                hash ??= GetJsonString(response, "hash");
                html ??= GetJsonString(response, "html");
                errorMessage ??= GetJsonString(response, "errorMsg") ??
                                 GetJsonString(response, "errormsg") ??
                                 GetJsonString(response, "message");
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<HelpRsaKey?> RequestHelpRsaKeyAsync(
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        string sessionId,
        string loginName,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionid"] = sessionId,
            ["wizard_ajax"] = "1",
            ["username"] = loginName
        };

        using var response = await SendHelpFormAsync(
                HttpMethod.Post,
                "https://help.steampowered.com/en/login/getrsakey/",
                bundle,
                webSession,
                form,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!TryParseWizardAjaxResponse(body, out var success, out _, out _, out _) || !success)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var modulus = GetJsonString(root, "publickey_mod");
            var exponent = GetJsonString(root, "publickey_exp");
            var timestamp = GetJsonString(root, "timestamp");

            if (string.IsNullOrWhiteSpace(modulus) ||
                string.IsNullOrWhiteSpace(exponent) ||
                string.IsNullOrWhiteSpace(timestamp))
            {
                return null;
            }

            return new HelpRsaKey(modulus, exponent, timestamp);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string EncryptPasswordWithRsa(string password, string modulusHex, string exponentHex)
    {
        try
        {
            var modulus = Convert.FromHexString(modulusHex);
            var exponent = Convert.FromHexString(exponentHex);
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = modulus,
                Exponent = exponent
            });

            var encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool TryExtractPasswordChangeSubmitContext(
        string html,
        PasswordRecoveryContext recoveryContext,
        SteamSessionBundle bundle,
        out PasswordChangeSubmitContext context)
    {
        context = default!;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var sessionToken = recoveryContext.SessionToken;
        var accountId = recoveryContext.AccountId;
        var loginName = bundle.LoginName;

        var match = SubmitPasswordChangeCallRegex.Match(html);
        if (match.Success)
        {
            sessionToken = match.Groups["s"].Value;
            accountId = match.Groups["account"].Value;
            loginName = WebUtility.HtmlDecode(match.Groups["login"].Value);
        }
        else
        {
            var hidden = ExtractHiddenInputs(html);
            if (hidden.TryGetValue("s", out var hiddenS) && !string.IsNullOrWhiteSpace(hiddenS))
            {
                sessionToken = hiddenS;
            }

            if (hidden.TryGetValue("account", out var hiddenAccount) && !string.IsNullOrWhiteSpace(hiddenAccount))
            {
                accountId = hiddenAccount;
            }
        }

        if (string.IsNullOrWhiteSpace(loginName))
        {
            var userInfoMatch = Regex.Match(
                html,
                @"""account_name""\s*:\s*""(?<login>[^""]+)""",
                RegexOptions.IgnoreCase);
            if (userInfoMatch.Success)
            {
                loginName = userInfoMatch.Groups["login"].Value;
            }
        }

        if (string.IsNullOrWhiteSpace(loginName))
        {
            loginName = bundle.AccountName;
        }

        if (string.IsNullOrWhiteSpace(sessionToken) ||
            string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(loginName))
        {
            return false;
        }

        context = new PasswordChangeSubmitContext(sessionToken, accountId, loginName);
        return true;
    }

    private static string? GetJsonString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static string ClassifyPasswordWizardError(string errorMessage, out bool retryable)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            retryable = false;
            return SteamReasonCodes.EndpointRejected;
        }

        if (errorMessage.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("too many", StringComparison.OrdinalIgnoreCase))
        {
            retryable = true;
            return SteamReasonCodes.AntiBotBlocked;
        }

        if (errorMessage.Contains("code", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            retryable = true;
            return SteamReasonCodes.GuardPending;
        }

        if (errorMessage.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("authorize", StringComparison.OrdinalIgnoreCase))
        {
            retryable = true;
            return SteamReasonCodes.AuthSessionMissing;
        }

        retryable = false;
        return SteamReasonCodes.EndpointRejected;
    }

    private static string BuildHelpHashUrl(string hash)
    {
        var trimmed = hash.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"https://help.steampowered.com/en/{trimmed.TrimStart('/')}";
    }

    private static bool LooksLikePasswordChangedHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var normalized = html.ToLowerInvariant();
        return normalized.Contains("password has been changed", StringComparison.Ordinal) ||
               normalized.Contains("password was changed", StringComparison.Ordinal) ||
               normalized.Contains("your password has been changed", StringComparison.Ordinal) ||
               normalized.Contains("successfully changed your password", StringComparison.Ordinal);
    }

    private static string SelectPasswordFailureReason(SteamOperationResult wizardAttempt, SteamOperationResult storeAttempt)
    {
        if (!string.IsNullOrWhiteSpace(wizardAttempt.ReasonCode) &&
            (string.Equals(wizardAttempt.ReasonCode, SteamReasonCodes.GuardPending, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(wizardAttempt.ReasonCode, SteamReasonCodes.AuthSessionMissing, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(wizardAttempt.ReasonCode, SteamReasonCodes.AntiBotBlocked, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(wizardAttempt.ReasonCode, SteamReasonCodes.Timeout, StringComparison.OrdinalIgnoreCase)))
        {
            return wizardAttempt.ReasonCode;
        }

        if (!string.IsNullOrWhiteSpace(storeAttempt.ReasonCode))
        {
            return storeAttempt.ReasonCode;
        }

        if (!string.IsNullOrWhiteSpace(wizardAttempt.ReasonCode))
        {
            return wizardAttempt.ReasonCode;
        }

        return SteamReasonCodes.Unknown;
    }

    private async Task<string?> EnsureHelpSessionAuthenticatedAsync(
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        CancellationToken cancellationToken)
    {
        const string wizardUrl = "https://help.steampowered.com/en/wizard/HelpChangePassword";
        var html = await GetHelpPageHtmlAsync(wizardUrl, bundle, webSession, cancellationToken).ConfigureAwait(false);
        var isLogin = LooksLikeHelpLoginPage(html) ||
                      (webSession.LastUri is { } firstUri &&
                       firstUri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase));
        if (!isLogin)
        {
            return html;
        }

        var transferUrl =
            "https://store.steampowered.com/login/transfer?redir=https%3A%2F%2Fhelp.steampowered.com%2Fen%2Fwizard%2FHelpChangePassword&origin=https%3A%2F%2Fhelp.steampowered.com";
        using (var transferResponse = await SendStoreRequestAsync(
                   HttpMethod.Get,
                   transferUrl,
                   bundle,
                   webSession,
                   content: null,
                   allowRedirects: true,
                   cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Steam password help-session transfer. flow={Flow} host={Host} path={Path} status={StatusCode}",
                "support_wizard",
                transferResponse.RequestMessage?.RequestUri?.Host,
                transferResponse.RequestMessage?.RequestUri?.AbsolutePath,
                (int)transferResponse.StatusCode);
        }

        html = await GetHelpPageHtmlAsync(wizardUrl, bundle, webSession, cancellationToken).ConfigureAwait(false);
        if (LooksLikeHelpLoginPage(html) ||
            (webSession.LastUri is { } secondUri &&
             secondUri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return html;
    }

    private static string? ExtractPasswordEmailConfirmationEndpoint(string html, Uri wizardUri)
    {
        var endpoints = ExtractWizardEndpoints(html, wizardUri, Array.Empty<HtmlFormCandidate>());
        var english = endpoints.FirstOrDefault(x =>
            x.Contains("/en/wizard/HelpWithLoginInfoEnterCode", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(english))
        {
            return english;
        }

        return endpoints.FirstOrDefault(x =>
            x.Contains("HelpWithLoginInfoEnterCode", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ExtractWizardEndpoints(
        string html,
        Uri wizardUri,
        IReadOnlyList<HtmlFormCandidate> formCandidates)
    {
        var context = TryGetQueryParameter(wizardUri, "context");
        var endpoints = new List<string>();
        foreach (var candidate in formCandidates)
        {
            if (!Uri.TryCreate(candidate.Action, UriKind.Absolute, out var absolute) ||
                !absolute.Host.Equals("help.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
                !absolute.AbsolutePath.Contains("/wizard/", StringComparison.OrdinalIgnoreCase) ||
                absolute.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            endpoints.Add(AppendQueryIfMissing(absolute, "context", context));
        }

        foreach (Match match in WizardEndpointRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var absolute = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : $"https://help.steampowered.com{(raw.StartsWith("/", StringComparison.Ordinal) ? raw : "/" + raw)}";

            if (!Uri.TryCreate(absolute, UriKind.Absolute, out var parsed) ||
                !parsed.Host.Equals("help.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
                !parsed.AbsolutePath.Contains("/wizard/", StringComparison.OrdinalIgnoreCase) ||
                parsed.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            endpoints.Add(AppendQueryIfMissing(parsed, "context", context));
        }

        var fallback = new[]
        {
            "https://help.steampowered.com/en/wizard/AjaxChangePassword",
            "https://help.steampowered.com/en/wizard/AjaxHelpChangePassword",
            "https://help.steampowered.com/en/wizard/AjaxVerifyPasswordAndChange"
        };
        foreach (var candidate in fallback)
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            {
                endpoints.Add(AppendQueryIfMissing(parsed, "context", context));
            }
        }

        return endpoints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<HtmlFormCandidate> ExtractStorePasswordCandidates(string html)
    {
        var candidates = new List<HtmlFormCandidate>();
        var forms = ExtractFormCandidates(
            html,
            "https://store.steampowered.com",
            form => form.Action.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    form.Action.Contains("change", StringComparison.OrdinalIgnoreCase) ||
                    form.Action.Contains("ajax", StringComparison.OrdinalIgnoreCase));

        foreach (var form in forms)
        {
            if (IsAcceptedStorePasswordSubmitEndpoint(form.Action))
            {
                candidates.Add(form);
            }
        }

        foreach (Match match in StorePasswordEndpointRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var absolute = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : $"https://store.steampowered.com{(raw.StartsWith("/", StringComparison.Ordinal) ? raw : "/" + raw)}";
            if (!IsAcceptedStorePasswordSubmitEndpoint(absolute))
            {
                continue;
            }

            candidates.Add(new HtmlFormCandidate(absolute, "POST", new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        return candidates
            .GroupBy(x => $"{x.Method}:{x.Action}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsAcceptedStorePasswordSubmitEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Equals("/account/changepassword", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("change", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("ajax", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendQueryIfMissing(Uri uri, string parameterName, string? parameterValue)
    {
        if (string.IsNullOrWhiteSpace(parameterValue))
        {
            return uri.ToString();
        }

        if (!string.IsNullOrWhiteSpace(TryGetQueryParameter(uri, parameterName)))
        {
            return uri.ToString();
        }

        var separator = string.IsNullOrWhiteSpace(uri.Query) ? "?" : "&";
        return uri + $"{separator}{parameterName}={Uri.EscapeDataString(parameterValue)}";
    }

    private static string? TryGetQueryParameter(Uri uri, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(uri.Query))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = pair.IndexOf('=');
            var key = index >= 0 ? pair[..index] : pair;
            if (!key.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = index >= 0 ? pair[(index + 1)..] : string.Empty;
            return Uri.UnescapeDataString(value);
        }

        return null;
    }

    private static bool LooksLikeCommunityLoginPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var normalized = html.ToLowerInvariant();
        if (LoginTitleRegex.IsMatch(html))
        {
            return true;
        }

        return normalized.Contains("steamcommunity.com/login", StringComparison.Ordinal) ||
               normalized.Contains("steamcommunity.com/openid/login", StringComparison.Ordinal) ||
               normalized.Contains("newlogindialog", StringComparison.Ordinal) ||
               normalized.Contains("login_home_page", StringComparison.Ordinal) ||
               normalized.Contains("join steam", StringComparison.Ordinal) ||
               normalized.Contains("global_header_login", StringComparison.Ordinal) ||
               normalized.Contains("featuretarget=\"login\"", StringComparison.Ordinal) ||
               normalized.Contains("feature-target=\"login\"", StringComparison.Ordinal) ||
               normalized.Contains("g_rgprofiledata = null", StringComparison.Ordinal) ||
               (normalized.Contains("name=\"password\"", StringComparison.Ordinal) &&
                normalized.Contains("sign in", StringComparison.Ordinal));
    }

    private static bool LooksLikeGamesUnauthorizedPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (LooksLikeCommunityLoginPage(html))
        {
            return true;
        }

        var normalized = html.ToLowerInvariant();
        var hasGamesSignals =
            normalized.Contains("rggames", StringComparison.Ordinal) ||
            normalized.Contains("gamecount", StringComparison.Ordinal) ||
            normalized.Contains("gameslistrowitem", StringComparison.Ordinal) ||
            normalized.Contains("profile_games", StringComparison.Ordinal) ||
            normalized.Contains("\"ownedgames\"", StringComparison.Ordinal);

        var hasGuestSignals =
            normalized.Contains("login to your steam account", StringComparison.Ordinal) ||
            normalized.Contains("you must be logged in", StringComparison.Ordinal) ||
            normalized.Contains("join steam", StringComparison.Ordinal) ||
            normalized.Contains("login_home_page", StringComparison.Ordinal) ||
            normalized.Contains("feature-target=\"login\"", StringComparison.Ordinal);

        return hasGuestSignals && !hasGamesSignals;
    }

    private static string? TryReadProfileUrlFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var direct = DirectProfileUrlRegex.Match(html);
        if (direct.Success)
        {
            return WebUtility.HtmlDecode(direct.Value);
        }

        var ogUrlMatch = Regex.Match(
            html,
            "<meta[^>]+property=(?:\"|')og:url(?:\"|')[^>]+content=(?:\"|')(?<url>https?://steamcommunity\\.com/(?:id|profiles)/[^\"']+)(?:\"|')",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (ogUrlMatch.Success)
        {
            return WebUtility.HtmlDecode(ogUrlMatch.Groups["url"].Value);
        }

        var jsonUrlMatch = Regex.Match(
            html,
            "\"profile_url\"\\s*:\\s*\"(?<url>https?:\\\\/\\\\/steamcommunity\\.com\\\\/(?:id|profiles)\\\\/[^\"\\\\]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (jsonUrlMatch.Success)
        {
            return Regex.Unescape(jsonUrlMatch.Groups["url"].Value);
        }

        return null;
    }

    private static SteamGatewayOperationException ClassifyGatewayException(Exception exception, string message)
    {
        if (exception is SteamGatewayOperationException operationException)
        {
            return operationException;
        }

        if (exception is TimeoutException or TaskCanceledException or OperationCanceledException)
        {
            return new SteamGatewayOperationException(
                message,
                SteamReasonCodes.Timeout,
                retryable: true,
                exception);
        }

        var raw = exception.Message;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (raw.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamGatewayOperationException(
                    message,
                    SteamReasonCodes.AntiBotBlocked,
                    retryable: true,
                    exception);
            }

            if (raw.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("not logged on", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("session", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamGatewayOperationException(
                    message,
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true,
                    exception);
            }
        }

        return new SteamGatewayOperationException(
            message,
            SteamReasonCodes.Unknown,
            retryable: true,
            exception);
    }

    private static SteamGatewayOperationException CreateLogonFailureException(EResult result, EResult extendedResult)
    {
        var reasonCode = MapLogonFailureReason(result, extendedResult, out var retryable);
        var effective = extendedResult == EResult.Invalid ? result : extendedResult;
        var message = $"Steam CM logon failed: result={result}; extended={effective}.";
        return new SteamGatewayOperationException(message, reasonCode, retryable);
    }

    private static string MapLogonFailureReason(EResult result, EResult extendedResult, out bool retryable)
    {
        var effective = extendedResult == EResult.Invalid ? result : extendedResult;
        switch (effective)
        {
            case EResult.InvalidPassword:
            case EResult.Expired:
            case EResult.AccessDenied:
                retryable = true;
                return SteamReasonCodes.AuthSessionMissing;
            case EResult.InvalidLoginAuthCode:
            case EResult.TwoFactorCodeMismatch:
            case EResult.AccountLogonDenied:
            case EResult.AccountLoginDeniedNeedTwoFactor:
                retryable = true;
                return SteamReasonCodes.GuardPending;
            case EResult.RateLimitExceeded:
                retryable = true;
                return SteamReasonCodes.AntiBotBlocked;
            case EResult.Timeout:
            case EResult.ServiceUnavailable:
                retryable = true;
                return SteamReasonCodes.Timeout;
            default:
                retryable = false;
                return SteamReasonCodes.Unknown;
        }
    }

    private static string ResolveLogOnUsername(SteamSessionBundle bundle)
    {
        if (!string.IsNullOrWhiteSpace(bundle.LoginName))
        {
            return bundle.LoginName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(bundle.AccountName))
        {
            return bundle.AccountName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(bundle.GuardData))
        {
            try
            {
                using var guard = JsonDocument.Parse(bundle.GuardData);
                foreach (var key in new[] { "account_name", "accountName", "username", "login" })
                {
                    if (!guard.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var candidate = value.GetString();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        return candidate.Trim();
                    }
                }
            }
            catch (JsonException)
            {
                // ignored: guardData may be non-JSON.
            }
        }

        if (!string.IsNullOrWhiteSpace(bundle.SteamId64))
        {
            return bundle.SteamId64.Trim();
        }

        var steamIdFromToken = TryGetSteamIdFromJwt(bundle.RefreshToken) ?? TryGetSteamIdFromJwt(bundle.AccessToken);
        return steamIdFromToken ?? string.Empty;
    }

    private static bool LooksLikeHelpLoginPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var normalized = html.ToLowerInvariant();
        if (LoginTitleRegex.IsMatch(html))
        {
            return true;
        }

        return normalized.Contains("help.steampowered.com/login", StringComparison.Ordinal) ||
               normalized.Contains("steamcommunity.com/openid/login", StringComparison.Ordinal) ||
               normalized.Contains("newlogindialog", StringComparison.Ordinal) ||
               normalized.Contains("login_home_page", StringComparison.Ordinal) ||
               (normalized.Contains("name=\"password\"", StringComparison.Ordinal) &&
                normalized.Contains("sign in", StringComparison.Ordinal));
    }

    private static bool LooksLikeStoreLoginPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var normalized = html.ToLowerInvariant();
        if (LoginTitleRegex.IsMatch(html))
        {
            return true;
        }

        return normalized.Contains("store.steampowered.com/login", StringComparison.Ordinal) ||
               normalized.Contains("login.steampowered.com", StringComparison.Ordinal) ||
               normalized.Contains("global_header_login", StringComparison.Ordinal) ||
               normalized.Contains("name=\"password\"", StringComparison.Ordinal);
    }

    private static bool LooksLikeGuardPending(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var normalized = body.ToLowerInvariant();
        return normalized.Contains("steam guard", StringComparison.Ordinal) ||
               normalized.Contains("two-factor", StringComparison.Ordinal) ||
               normalized.Contains("mobile authenticator", StringComparison.Ordinal) ||
               normalized.Contains("guard code", StringComparison.Ordinal) ||
               normalized.Contains("check your steam mobile app", StringComparison.Ordinal) ||
               normalized.Contains("check your email", StringComparison.Ordinal) ||
               normalized.Contains("confirm this change", StringComparison.Ordinal) ||
               normalized.Contains("email verification", StringComparison.Ordinal) ||
               normalized.Contains("verification code", StringComparison.Ordinal) ||
               normalized.Contains("email an account verification code", StringComparison.Ordinal) ||
               normalized.Contains("how would you like to change your password", StringComparison.Ordinal) ||
               normalized.Contains("helpwithlogininfoentercode", StringComparison.Ordinal) ||
               normalized.Contains("helpwithlogininfosendcode", StringComparison.Ordinal);
    }

    private static bool LooksLikeAntiBot(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var normalized = body.ToLowerInvariant();
        return normalized.Contains("captcha", StringComparison.Ordinal) ||
               normalized.Contains("cloudflare", StringComparison.Ordinal) ||
               normalized.Contains("access denied", StringComparison.Ordinal) ||
               normalized.Contains("too many requests", StringComparison.Ordinal) ||
               normalized.Contains("unusual traffic", StringComparison.Ordinal) ||
               normalized.Contains("rate limit", StringComparison.Ordinal);
    }

    private static List<HtmlFormCandidate> ExtractFormCandidates(
        string html,
        string baseUrl,
        Func<HtmlFormCandidate, bool>? filter = null)
    {
        var forms = new List<HtmlFormCandidate>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return forms;
        }

        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        foreach (Match formMatch in HtmlFormRegex.Matches(html))
        {
            if (!formMatch.Success)
            {
                continue;
            }

            var attrs = formMatch.Groups["attrs"].Value;
            var formContent = formMatch.Groups["content"].Value;

            var action = string.Empty;
            var actionMatch = FormActionRegex.Match(attrs);
            if (actionMatch.Success)
            {
                action = WebUtility.HtmlDecode(actionMatch.Groups["value"].Value);
            }

            var method = "POST";
            var methodMatch = FormMethodRegex.Match(attrs);
            if (methodMatch.Success)
            {
                method = methodMatch.Groups["value"].Value.Trim();
            }

            Uri actionUri;
            if (string.IsNullOrWhiteSpace(action))
            {
                actionUri = baseUri;
            }
            else if (Uri.TryCreate(action, UriKind.Absolute, out var absolute))
            {
                actionUri = absolute;
            }
            else
            {
                actionUri = new Uri(baseUri, action);
            }

            var hidden = ExtractHiddenInputs(formContent);
            var candidate = new HtmlFormCandidate(actionUri.ToString(), method.ToUpperInvariant(), hidden);
            if (filter is null || filter(candidate))
            {
                forms.Add(candidate);
            }
        }

        return forms;
    }

    private async Task<SteamFriendInviteLink?> TryGetFriendInviteLinkFromWebAsync(
        SteamSessionBundle bundle,
        CancellationToken cancellationToken)
    {
        using var webSession = CreateWebSession(bundle);

        using var response = await SendCommunityRequestAsync(
                HttpMethod.Get,
                "https://steamcommunity.com/my/friends/add",
                bundle,
                webSession,
                content: null,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            path.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeCommunityLoginPage(body))
        {
            throw new SteamGatewayOperationException(
                "Steam community session is not authorized for invite link retrieval.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new SteamGatewayOperationException(
                $"Steam blocked invite link page: HTTP {(int)response.StatusCode}.",
                SteamReasonCodes.AntiBotBlocked,
                retryable: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SteamGatewayOperationException(
                $"Steam invite page returned HTTP {(int)response.StatusCode}.",
                SteamReasonCodes.EndpointRejected,
                retryable: false);
        }

        if (!TryExtractQuickInviteLinkFromHtml(body, out var inviteUrl))
        {
            return null;
        }

        if (!TryParseQuickInviteLink(inviteUrl, out _, out var inviteToken))
        {
            throw new SteamGatewayOperationException(
                "Steam invite page returned malformed quick-invite link.",
                SteamReasonCodes.EndpointRejected,
                retryable: false);
        }

        var codeMatch = QuickInviteLinkRegex.Match(inviteUrl);
        var inviteCode = codeMatch.Success ? NormalizeInviteUrlSegment(codeMatch.Groups["code"].Value) : string.Empty;
        var sessionId = ExtractSessionId(body) ?? bundle.SessionId;
        if (string.IsNullOrWhiteSpace(inviteToken))
        {
            var resolvedToken = await TryGetOrCreateInviteTokenAsync(bundle, webSession, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolvedToken))
            {
                inviteToken = resolvedToken;
                inviteUrl = string.IsNullOrWhiteSpace(inviteCode)
                    ? inviteUrl
                    : $"https://s.team/p/{inviteCode}/{inviteToken}";
            }
        }

        return new SteamFriendInviteLink
        {
            InviteUrl = inviteUrl,
            InviteCode = inviteCode,
            InviteToken = inviteToken,
            SyncedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<string?> TryGetOrCreateInviteTokenAsync(
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var fromExisting = await TryGetInviteTokenFromExistingInvitesAsync(
                bundle,
                webSession,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromExisting))
        {
            return fromExisting;
        }

        return await TryCreateInviteTokenAsync(
                bundle,
                webSession,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> TryGetInviteTokenFromExistingInvitesAsync(
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var url = $"https://steamcommunity.com/invites/ajaxgetall?sessionid={Uri.EscapeDataString(sessionId)}";
        using var response = await SendCommunityRequestAsync(
                HttpMethod.Get,
                url,
                bundle,
                webSession,
                content: null,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var responseUri = response.RequestMessage?.RequestUri;
        var responsePath = responseUri?.AbsolutePath ?? string.Empty;

        logger.LogInformation(
            "Steam invite flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
            "community_invites_get_all",
            responseUri?.Host,
            responsePath,
            (int)response.StatusCode);

        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeCommunityLoginPage(body))
        {
            throw new SteamGatewayOperationException(
                "Steam community session is unauthorized for invite link retrieval.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new SteamGatewayOperationException(
                "Steam blocked invite token retrieval endpoint.",
                SteamReasonCodes.AntiBotBlocked,
                retryable: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (TryExtractInviteTokenFromAjaxGetAll(body, out var inviteToken))
        {
            return inviteToken;
        }

        return null;
    }

    private async Task<string?> TryCreateInviteTokenAsync(
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionid"] = sessionId,
            ["steamid_user"] = bundle.SteamId64,
            ["duration"] = (30 * 24 * 60 * 60).ToString(CultureInfo.InvariantCulture)
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await SendCommunityRequestAsync(
                HttpMethod.Post,
                "https://steamcommunity.com/invites/ajaxcreate",
                bundle,
                webSession,
                content,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var responseUri = response.RequestMessage?.RequestUri;
        var responsePath = responseUri?.AbsolutePath ?? string.Empty;

        logger.LogInformation(
            "Steam invite flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
            "community_invites_create",
            responseUri?.Host,
            responsePath,
            (int)response.StatusCode);

        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeCommunityLoginPage(body))
        {
            throw new SteamGatewayOperationException(
                "Steam community session is unauthorized for invite link creation.",
                SteamReasonCodes.AuthSessionMissing,
                retryable: true);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new SteamGatewayOperationException(
                "Steam blocked invite token creation endpoint.",
                SteamReasonCodes.AntiBotBlocked,
                retryable: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (LooksLikeAntiBot(body))
        {
            throw new SteamGatewayOperationException(
                "Steam anti-bot blocked invite token creation.",
                SteamReasonCodes.AntiBotBlocked,
                retryable: true);
        }

        if (TryExtractInviteTokenFromAjaxCreate(body, out var inviteToken))
        {
            return inviteToken;
        }

        return null;
    }

    private async Task<SteamOperationResult> TryAcceptFriendInviteViaCommunityWebAsync(
        SteamSessionBundle bundle,
        ulong sourceSteamId,
        CancellationToken cancellationToken)
    {
        var sourceSteamIdRaw = sourceSteamId.ToString(CultureInfo.InvariantCulture);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steamid"] = sourceSteamIdRaw,
            ["sessionID"] = bundle.SessionId,
            ["sessionid"] = bundle.SessionId
        };

        using var response = await SendCommunityFormAsync(
                HttpMethod.Post,
                "https://steamcommunity.com/actions/AddFriendAjax",
                bundle,
                form,
                MediaTypeNames.Application.FormUrlEncoded,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var responseUri = response.RequestMessage?.RequestUri;
        var responsePath = responseUri?.AbsolutePath ?? string.Empty;
        logger.LogInformation(
            "Steam friends flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
            "community_add_friend",
            responseUri?.Host,
            responsePath,
            (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                TryParseAddFriendAjaxResponse(body, sourceSteamIdRaw, out var parsedError))
            {
                if (string.IsNullOrWhiteSpace(parsedError))
                {
                    return new SteamOperationResult
                    {
                        Success = true,
                        ReasonCode = SteamReasonCodes.None,
                        Data = new Dictionary<string, string>
                        {
                            ["sourceSteamId"] = sourceSteamIdRaw,
                            ["flow"] = "community_add_friend"
                        }
                    };
                }

                return Failure(parsedError, SteamReasonCodes.EndpointRejected);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                return Failure("Steam temporarily blocked friend invite action.", SteamReasonCodes.AntiBotBlocked, retryable: true);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("Steam community session is unauthorized for friend invite action.", SteamReasonCodes.AuthSessionMissing, retryable: true);
            }

            if (response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                return Failure($"Steam AddFriendAjax endpoint rejected: HTTP {(int)response.StatusCode}.", SteamReasonCodes.EndpointRejected);
            }

            return Failure($"Steam AddFriendAjax request failed: HTTP {(int)response.StatusCode}.", SteamReasonCodes.EndpointRejected, retryable: true);
        }

        if (responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeCommunityLoginPage(body))
        {
            return Failure("Steam community session is unauthorized for friend invite action.", SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (LooksLikeAntiBot(body))
        {
            return Failure("Steam anti-bot blocked friend invite action.", SteamReasonCodes.AntiBotBlocked, retryable: true);
        }

        if (!TryParseAddFriendAjaxResponse(body, sourceSteamIdRaw, out var addFriendError))
        {
            return Failure("Steam returned malformed AddFriendAjax response.", SteamReasonCodes.EndpointRejected);
        }

        if (!string.IsNullOrWhiteSpace(addFriendError))
        {
            return Failure(addFriendError, SteamReasonCodes.EndpointRejected);
        }

        return new SteamOperationResult
        {
            Success = true,
            ReasonCode = SteamReasonCodes.None,
            Data = new Dictionary<string, string>
            {
                ["sourceSteamId"] = sourceSteamIdRaw,
                ["flow"] = "community_add_friend"
            }
        };
    }

    private static bool TryExtractQuickInviteLinkFromHtml(string html, out string inviteUrl)
    {
        inviteUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var decoded = WebUtility.HtmlDecode(html);
        var direct = QuickInviteLinkFromHtmlRegex.Match(decoded);
        if (TryBuildInviteUrlFromMatch(direct, out inviteUrl))
        {
            return true;
        }

        var escaped = QuickInviteEscapedRegex.Match(html);
        if (TryBuildInviteUrlFromMatch(escaped, out inviteUrl))
        {
            return true;
        }

        var shortUrlMatch = QuickInviteShortUrlJsonRegex.Match(decoded);
        if (shortUrlMatch.Success)
        {
            var shortUrl = NormalizeInviteUrl(shortUrlMatch.Groups["url"].Value);
            if (!string.IsNullOrWhiteSpace(shortUrl))
            {
                inviteUrl = shortUrl;
                return true;
            }
        }

        if (TryExtractInviteUrlFromUserInfoAttributes(html, out inviteUrl) ||
            TryExtractInviteUrlFromUserInfoAttributes(decoded, out inviteUrl))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseQuickInviteLink(string inviteUrl, out ulong sourceSteamId, out string inviteToken)
    {
        sourceSteamId = 0;
        inviteToken = string.Empty;

        if (string.IsNullOrWhiteSpace(inviteUrl))
        {
            return false;
        }

        var match = QuickInviteLinkRegex.Match(inviteUrl.Trim());
        if (!match.Success)
        {
            return false;
        }

        var friendCode = match.Groups["code"].Value.Trim().ToLowerInvariant();
        if (!ParseFriendCode(friendCode, out sourceSteamId))
        {
            return false;
        }

        inviteToken = match.Groups["token"].Success
            ? Uri.UnescapeDataString(match.Groups["token"].Value.Trim())
            : string.Empty;
        return true;
    }

    private static bool TryBuildInviteUrlFromMatch(Match match, out string inviteUrl)
    {
        inviteUrl = string.Empty;
        if (!match.Success)
        {
            return false;
        }

        var codeRaw = match.Groups["code"].Value;
        var tokenRaw = match.Groups["token"].Success ? match.Groups["token"].Value : string.Empty;
        var code = NormalizeInviteUrlSegment(codeRaw);
        var token = NormalizeInviteUrlSegment(tokenRaw);
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        inviteUrl = string.IsNullOrWhiteSpace(token)
            ? $"https://s.team/p/{code}"
            : $"https://s.team/p/{code}/{token}";
        return true;
    }

    private static bool TryExtractInviteUrlFromUserInfoAttributes(string html, out string inviteUrl)
    {
        inviteUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        foreach (Match match in DataUserInfoAttributeRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var rawJson = WebUtility.HtmlDecode(match.Groups["json"].Value);
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(rawJson);
                var root = json.RootElement;

                var shortUrl = GetJsonString(root, "short_url");
                if (!string.IsNullOrWhiteSpace(shortUrl))
                {
                    var normalized = NormalizeInviteUrl(shortUrl);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        inviteUrl = normalized;
                        return true;
                    }
                }

                var steamIdRaw = GetJsonString(root, "steamid");
                if (ulong.TryParse(steamIdRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId64))
                {
                    inviteUrl = $"https://s.team/p/{CreateFriendCode(steamId64)}";
                    return true;
                }
            }
            catch (JsonException)
            {
                // ignore malformed userinfo blob and continue scanning.
            }
        }

        return false;
    }

    private static bool TryExtractInviteTokenFromAjaxGetAll(string body, out string inviteToken)
    {
        inviteToken = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in tokens.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var token = GetJsonString(item, "invite_token") ??
                            GetJsonString(item, "token");
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var valid = true;
                if (item.TryGetProperty("valid", out var validNode))
                {
                    valid = IsJsonTruthy(validNode);
                }

                if (!valid)
                {
                    continue;
                }

                inviteToken = NormalizeInviteUrlSegment(token);
                if (!string.IsNullOrWhiteSpace(inviteToken))
                {
                    return true;
                }
            }

            foreach (var item in tokens.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var token = GetJsonString(item, "invite_token") ??
                            GetJsonString(item, "token");
                inviteToken = NormalizeInviteUrlSegment(token ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(inviteToken))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryExtractInviteTokenFromAjaxCreate(string body, out string inviteToken)
    {
        inviteToken = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (root.TryGetProperty("invite", out var inviteNode) &&
                inviteNode.ValueKind == JsonValueKind.Object)
            {
                var nestedToken = GetJsonString(inviteNode, "invite_token") ??
                                  GetJsonString(inviteNode, "token");
                inviteToken = NormalizeInviteUrlSegment(nestedToken ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(inviteToken))
                {
                    return true;
                }
            }

            var direct = GetJsonString(root, "invite_token") ??
                         GetJsonString(root, "token");
            inviteToken = NormalizeInviteUrlSegment(direct ?? string.Empty);
            return !string.IsNullOrWhiteSpace(inviteToken);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeInviteUrlSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = WebUtility.HtmlDecode(value.Trim())
            .Replace("\\/", "/", StringComparison.Ordinal);
        normalized = Regex.Unescape(normalized);
        return normalized.Trim().Trim('/', '"', '\'', '{', '}');
    }

    private static string NormalizeInviteUrl(string value)
    {
        var normalized = NormalizeInviteUrlSegment(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.StartsWith("s.team/p/", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{normalized}";
        }

        return $"https://s.team/p/{normalized}";
    }

    private static bool TryParseAddFriendAjaxResponse(string body, string targetSteamId, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            errorMessage = "Steam AddFriendAjax returned empty response.";
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (!TryReadSuccessFlag(root))
            {
                errorMessage = GetJsonString(root, "error") ??
                               GetJsonString(root, "errormsg") ??
                               GetJsonString(root, "message") ??
                               "Steam did not confirm friend invite action.";
                return true;
            }

            if (root.TryGetProperty("failed_invites", out var failedInvites) &&
                failedInvites.ValueKind == JsonValueKind.Array &&
                root.TryGetProperty("failed_invites_result", out var failedResults) &&
                failedResults.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var inviteElement in failedInvites.EnumerateArray())
                {
                    var failedSteamId = inviteElement.ValueKind == JsonValueKind.String ? inviteElement.GetString() : null;
                    var code = index < failedResults.GetArrayLength() && failedResults[index].TryGetInt32(out var parsed)
                        ? parsed
                        : 0;

                    if (string.Equals(failedSteamId, targetSteamId, StringComparison.Ordinal))
                    {
                        errorMessage = MapAddFriendFailureCode(code);
                        return true;
                    }

                    index++;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            errorMessage = "Steam AddFriendAjax returned malformed JSON.";
            return false;
        }
    }

    private static string MapAddFriendFailureCode(int code)
    {
        return code switch
        {
            8 => "Нельзя добавить в друзья этот же аккаунт (self-invite).",
            15 => "Steam временно ограничил отправку заявок в друзья.",
            24 => "Steam отклонил приглашение (возможны ограничения приватности или лимиты аккаунта).",
            _ => $"Steam отклонил приглашение в друзья (код {code})."
        };
    }

    private static string CreateFriendCode(ulong steamId64)
    {
        var accountId = (uint)(steamId64 & 0xFFFFFFFFul);
        var accountHex = accountId.ToString("x", CultureInfo.InvariantCulture);
        var code = new StringBuilder(accountHex.Length);

        foreach (var hexChar in accountHex)
        {
            var nibble = hexChar switch
            {
                >= '0' and <= '9' => hexChar - '0',
                >= 'a' and <= 'f' => 10 + hexChar - 'a',
                >= 'A' and <= 'F' => 10 + hexChar - 'A',
                _ => -1
            };

            if (nibble < 0 || nibble >= FriendCodeReplacements.Length)
            {
                throw new InvalidOperationException("Failed to create Steam quick-invite code.");
            }

            code.Append(FriendCodeReplacements[nibble]);
        }

        var friendCode = code.ToString();
        var dashPos = Math.Max(1, friendCode.Length / 2);
        return friendCode[..dashPos] + "-" + friendCode[dashPos..];
    }

    private static bool ParseFriendCode(string friendCode, out ulong steamId64)
    {
        steamId64 = 0;
        if (string.IsNullOrWhiteSpace(friendCode))
        {
            return false;
        }

        var normalized = friendCode.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        var hex = new StringBuilder(normalized.Length);
        foreach (var codeChar in normalized)
        {
            var index = Array.IndexOf(FriendCodeReplacements, codeChar);
            if (index < 0)
            {
                return false;
            }

            hex.Append(index.ToString("x", CultureInfo.InvariantCulture));
        }

        if (!uint.TryParse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var accountId))
        {
            return false;
        }

        steamId64 = accountId | 0x0110000100000000ul;
        return true;
    }

    private static bool TryParseSsrRenderContext(string html, out JsonDocument jsonDocument)
    {
        var direct = SsrRenderContextRegex.Match(html);
        if (direct.Success)
        {
            try
            {
                jsonDocument = JsonDocument.Parse(direct.Groups["json"].Value);
                return true;
            }
            catch (JsonException)
            {
                // continue with alternate parsing.
            }
        }

        var parseCall = SsrRenderContextJsonParseRegex.Match(html);
        if (parseCall.Success)
        {
            var encoded = parseCall.Groups["json"].Value;
            try
            {
                var decoded = Regex.Unescape(encoded);
                decoded = WebUtility.HtmlDecode(decoded);
                jsonDocument = JsonDocument.Parse(decoded);
                return true;
            }
            catch (JsonException)
            {
                // ignored
            }
        }

        jsonDocument = null!;
        return false;
    }

    private static void CollectOwnedGames(JsonElement element, List<SteamOwnedGame> buffer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (TryExtractOwnedGame(element, out var game))
                {
                    buffer.Add(game);
                }

                foreach (var prop in element.EnumerateObject())
                {
                    CollectOwnedGames(prop.Value, buffer);
                }

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    CollectOwnedGames(item, buffer);
                }

                break;
            }
        }
    }

    private static string? CollectOwnedGamesFromQueryData(JsonElement renderContextRoot, List<SteamOwnedGame> buffer)
    {
        if (renderContextRoot.ValueKind != JsonValueKind.Object ||
            !renderContextRoot.TryGetProperty("queryData", out var queryDataElement) ||
            queryDataElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var queryDataRaw = queryDataElement.GetString();
        if (string.IsNullOrWhiteSpace(queryDataRaw))
        {
            return null;
        }

        try
        {
            using var queryData = JsonDocument.Parse(queryDataRaw);
            if (!queryData.RootElement.TryGetProperty("queries", out var queries) ||
                queries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? profileUrl = null;
            foreach (var query in queries.EnumerateArray())
            {
                if (query.ValueKind != JsonValueKind.Object ||
                    !query.TryGetProperty("queryKey", out var queryKey) ||
                    queryKey.ValueKind != JsonValueKind.Array ||
                    queryKey.GetArrayLength() == 0 ||
                    queryKey[0].ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var key = queryKey[0].GetString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!query.TryGetProperty("state", out var state) ||
                    state.ValueKind != JsonValueKind.Object ||
                    !state.TryGetProperty("data", out var stateData))
                {
                    continue;
                }

                if (key.Equals("OwnedGames", StringComparison.OrdinalIgnoreCase))
                {
                    CollectOwnedGames(stateData, buffer);
                }
                else if (string.IsNullOrWhiteSpace(profileUrl) &&
                         key.Equals("PlayerLinkDetails", StringComparison.OrdinalIgnoreCase) &&
                         stateData.ValueKind == JsonValueKind.Object &&
                         stateData.TryGetProperty("public_data", out var publicData) &&
                         publicData.ValueKind == JsonValueKind.Object &&
                         publicData.TryGetProperty("profile_url", out var profileUrlElement) &&
                         profileUrlElement.ValueKind == JsonValueKind.String)
                {
                    profileUrl = profileUrlElement.GetString();
                }
            }

            return profileUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryExtractOwnedGame(JsonElement element, out SteamOwnedGame game)
    {
        game = new SteamOwnedGame();

        if (!TryReadInt(element, out var appId, "appid", "app_id", "appId"))
        {
            return false;
        }

        if (!TryReadString(element, out var name, "name", "title", "app_name"))
        {
            return false;
        }

        var playtime = 0;
        if (TryReadInt(element, out var minutes, "playtime_forever", "playtime_minutes", "playtime"))
        {
            playtime = minutes;
        }
        else if (TryReadDouble(element, out var hours, "hours_forever"))
        {
            playtime = (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        }

        TryReadString(element, out var icon, "img_icon_url", "img_logo_url", "icon");

        game = new SteamOwnedGame
        {
            AppId = appId,
            Name = name,
            PlaytimeMinutes = Math.Max(0, playtime),
            ImgIconUrl = icon
        };

        return true;
    }

    private static void CollectOwnedGamesFromRawHtml(string html, List<SteamOwnedGame> buffer)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        CollectOwnedGamesFromRgGames(html, buffer);

        var patterns = new[]
        {
            "\"appid\"\\s*:\\s*(?<id>\\d+)[^\\}]{0,800}?\"name\"\\s*:\\s*\"(?<name>(?:\\\\.|[^\"])*)\"",
            "\"name\"\\s*:\\s*\"(?<name>(?:\\\\.|[^\"])*)\"[^\\}]{0,800}?\"appid\"\\s*:\\s*(?<id>\\d+)",
            "'appid'\\s*:\\s*(?<id>\\d+)[^\\}]{0,800}?'name'\\s*:\\s*'(?<name>(?:\\\\.|[^'])*)'",
            "'name'\\s*:\\s*'(?<name>(?:\\\\.|[^'])*)'[^\\}]{0,800}?'appid'\\s*:\\s*(?<id>\\d+)"
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                if (!match.Success ||
                    !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
                {
                    continue;
                }

                var rawName = match.Groups["name"].Value;
                var name = DecodeJsString(rawName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var playtime = ExtractPlaytimeMinutesFromFragment(match.Value);
                buffer.Add(new SteamOwnedGame
                {
                    AppId = appId,
                    Name = name,
                    PlaytimeMinutes = playtime
                });
            }
        }
    }

    private static void CollectOwnedGamesFromRgGames(string html, List<SteamOwnedGame> buffer)
    {
        foreach (Match match in RgGamesJsonRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            CollectOwnedGamesFromJsonArray(match.Groups["json"].Value, buffer);
        }

        foreach (Match match in RgGamesJsonParseRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var encoded = match.Groups["json"].Value;
            var decoded = WebUtility.HtmlDecode(Regex.Unescape(encoded));
            CollectOwnedGamesFromJsonArray(decoded, buffer);
        }
    }

    private static void CollectOwnedGamesFromJsonArray(string json, List<SteamOwnedGame> buffer)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!TryExtractOwnedGame(item, out var game))
                {
                    continue;
                }

                buffer.Add(game);
            }
        }
        catch (JsonException)
        {
            // ignored
        }
    }

    private static int ExtractPlaytimeMinutesFromFragment(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return 0;
        }

        var playtimeMatch = Regex.Match(
            fragment,
            "(?:playtime_forever|playtime_minutes|playtime)\\s*[:=]\\s*(?<minutes>\\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (playtimeMatch.Success &&
            int.TryParse(playtimeMatch.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return Math.Max(0, minutes);
        }

        var hoursMatch = Regex.Match(
            fragment,
            "(?:hours_forever|hoursplayed)\\s*[:=]\\s*(?<hours>\\d+(?:\\.\\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (hoursMatch.Success &&
            double.TryParse(hoursMatch.Groups["hours"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var hours))
        {
            return (int)Math.Max(0, Math.Round(hours * 60, MidpointRounding.AwayFromZero));
        }

        return 0;
    }

    private static string DecodeJsString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var decoded = Regex.Unescape(value);
            return WebUtility.HtmlDecode(decoded);
        }
        catch
        {
            return value;
        }
    }

    private static string? TryReadProfileUrl(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "profile_url", "profileUrl", "ProfileURL" })
            {
                if (root.TryGetProperty(propName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }
            }

            foreach (var prop in root.EnumerateObject())
            {
                var nested = TryReadProfileUrl(prop.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var nested = TryReadProfileUrl(item);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryReadString(JsonElement root, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var s = element.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadInt(JsonElement root, out int value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var asInt))
            {
                value = asInt;
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out asInt))
            {
                value = asInt;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadDouble(JsonElement root, out double value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var asDouble))
            {
                value = asDouble;
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                double.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out asDouble))
            {
                value = asDouble;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryParseSuccessFromJson(string body, out string? errorMessage)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (TryReadSuccessFlag(root))
            {
                errorMessage = null;
                return true;
            }

            if (root.TryGetProperty("response", out var response) &&
                response.ValueKind == JsonValueKind.Object &&
                TryReadSuccessFlag(response))
            {
                errorMessage = null;
                return true;
            }

            if (TryReadErrorText(root, out errorMessage))
            {
                return false;
            }

            if (root.TryGetProperty("response", out response) &&
                response.ValueKind == JsonValueKind.Object &&
                TryReadErrorText(response, out errorMessage))
            {
                return false;
            }
        }
        catch (JsonException)
        {
            // ignored: not a JSON payload.
        }

        errorMessage = null;
        return false;
    }

    private static bool TryReadSuccessFlag(JsonElement root)
    {
        if (root.TryGetProperty("success", out var success) && IsJsonTruthy(success))
        {
            return true;
        }

        if (root.TryGetProperty("eresult", out var eresult))
        {
            if ((eresult.ValueKind == JsonValueKind.Number && eresult.TryGetInt32(out var asNumber) && asNumber == 1) ||
                (eresult.ValueKind == JsonValueKind.String &&
                 int.TryParse(eresult.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out asNumber) &&
                 asNumber == 1))
            {
                return true;
            }
        }

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            var value = status.GetString();
            if (string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "success", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsJsonTruthy(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var asInt))
        {
            return asInt != 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "success", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryReadErrorText(JsonElement root, out string? errorMessage)
    {
        foreach (var key in new[] { "message", "errmsg", "error", "errorMessage", "description", "detail" })
        {
            if (root.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String)
            {
                errorMessage = text.GetString();
                return !string.IsNullOrWhiteSpace(errorMessage);
            }
        }

        errorMessage = null;
        return false;
    }

    private static string? ExtractSessionId(string html)
    {
        var match = SessionIdRegex.Match(html);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static Dictionary<string, string> ExtractHiddenInputs(string html)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in HiddenInputRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var name = WebUtility.HtmlDecode(match.Groups["name"].Value);
            var value = WebUtility.HtmlDecode(match.Groups["value"].Value);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            dict[name] = value;
        }

        return dict;
    }

    private static string? ExtractFormActionUrl(string html, string keyword)
    {
        var formMatches = Regex.Matches(
            html,
            "<form[^>]*action=(?<quote>[\"'])(?<action>.*?)(?:\\k<quote>)[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in formMatches)
        {
            if (!match.Success)
            {
                continue;
            }

            var action = WebUtility.HtmlDecode(match.Groups["action"].Value);
            if (string.IsNullOrWhiteSpace(action))
            {
                continue;
            }

            if (!action.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return action.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? action
                : $"https://store.steampowered.com{action}";
        }

        return null;
    }

    private async Task<string> GetStorePageHtmlAsync(string url, SteamSessionBundle bundle, CancellationToken cancellationToken)
    {
        using var response = await SendStoreRequestAsync(
            HttpMethod.Get,
            url,
            bundle,
            content: null,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Steam store endpoint {url} returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetHelpPageHtmlAsync(string url, SteamSessionBundle bundle, CancellationToken cancellationToken)
    {
        using var response = await SendHelpRequestAsync(
            HttpMethod.Get,
            url,
            bundle,
            content: null,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Steam help endpoint {url} returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetStorePageHtmlAsync(string url, SteamSessionBundle bundle, SteamWebSession webSession, CancellationToken cancellationToken)
    {
        using var response = await SendStoreRequestAsync(
            HttpMethod.Get,
            url,
            bundle,
            webSession,
            content: null,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Steam store endpoint {url} returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetHelpPageHtmlAsync(string url, SteamSessionBundle bundle, SteamWebSession webSession, CancellationToken cancellationToken)
    {
        using var response = await SendHelpRequestAsync(
            HttpMethod.Get,
            url,
            bundle,
            webSession,
            content: null,
            allowRedirects: true,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Steam help endpoint {url} returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendStoreFormAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        IReadOnlyDictionary<string, string> form,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        using HttpContent content = new FormUrlEncodedContent(form);
        return await SendStoreRequestAsync(
            method,
            url,
            bundle,
            content,
            allowRedirects,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendHelpFormAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        IReadOnlyDictionary<string, string> form,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        using HttpContent content = new FormUrlEncodedContent(form);
        return await SendHelpRequestAsync(
            method,
            url,
            bundle,
            content,
            allowRedirects,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendStoreFormAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        IReadOnlyDictionary<string, string> form,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        using HttpContent content = new FormUrlEncodedContent(form);
        return await SendStoreRequestAsync(
            method,
            url,
            bundle,
            webSession,
            content,
            allowRedirects,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendHelpFormAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        IReadOnlyDictionary<string, string> form,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        using HttpContent content = new FormUrlEncodedContent(form);
        return await SendHelpRequestAsync(
            method,
            url,
            bundle,
            webSession,
            content,
            allowRedirects,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendStoreRequestAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        HttpContent? content,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirects,
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
        };

        var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(bundle));
        request.Headers.Referrer = new Uri("https://store.steampowered.com/account/");
        if (request.Content is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        }

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        request.Dispose();
        client.Dispose();
        handler.Dispose();
        return response;
    }

    private async Task<HttpResponseMessage> SendHelpRequestAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        HttpContent? content,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirects,
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
        };

        var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(bundle));
        request.Headers.Referrer = new Uri("https://help.steampowered.com/en/");
        if (request.Content is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://help.steampowered.com");
        }

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        request.Dispose();
        client.Dispose();
        handler.Dispose();
        return response;
    }

    private SteamWebSession CreateWebSession(SteamSessionBundle bundle)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer()
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
        };

        ApplySessionCookies(handler.CookieContainer, bundle);
        return new SteamWebSession(bundle, handler, client);
    }

    private async Task WarmupPasswordFlowAsync(SteamWebSession webSession, CancellationToken cancellationToken)
    {
        await SendStoreRequestAsync(
                HttpMethod.Get,
                "https://store.steampowered.com/account/",
                webSession.Bundle,
                webSession,
                content: null,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        await SendCommunityRequestAsync(
                HttpMethod.Get,
                "https://steamcommunity.com/my",
                webSession.Bundle,
                webSession,
                content: null,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        await SendHelpRequestAsync(
                HttpMethod.Get,
                "https://help.steampowered.com/en/",
                webSession.Bundle,
                webSession,
                content: null,
                allowRedirects: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (webSession.LastUri is { AbsolutePath: var path } &&
            path.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            var transferUrl =
                "https://store.steampowered.com/login/transfer?redir=https%3A%2F%2Fhelp.steampowered.com%2Fen%2Fwizard%2FHelpChangePassword&origin=https%3A%2F%2Fhelp.steampowered.com";
            await SendStoreRequestAsync(
                    HttpMethod.Get,
                    transferUrl,
                    webSession.Bundle,
                    webSession,
                    content: null,
                    allowRedirects: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendStoreRequestAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        HttpContent? content,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        return await SendWebRequestAsync(
            webSession,
            method,
            url,
            content,
            allowRedirects,
            origin: "https://store.steampowered.com",
            referrer: "https://store.steampowered.com/account/",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendHelpRequestAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        HttpContent? content,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        return await SendWebRequestAsync(
            webSession,
            method,
            url,
            content,
            allowRedirects,
            origin: "https://help.steampowered.com",
            referrer: "https://help.steampowered.com/en/",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWebRequestAsync(
        SteamWebSession webSession,
        HttpMethod method,
        string url,
        HttpContent? content,
        bool allowRedirects,
        string origin,
        string referrer,
        CancellationToken cancellationToken)
    {
        var currentUri = new Uri(url, UriKind.Absolute);
        var currentMethod = method;
        HttpContent? currentContent = content;

        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            HttpResponseMessage response;
            var maxAttempts = currentContent is null ? 2 : 1;
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(currentMethod, currentUri)
                {
                    Content = currentContent
                };

                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                request.Headers.Accept.ParseAdd("*/*");
                request.Headers.Referrer = new Uri(referrer);
                if (request.Content is not null)
                {
                    request.Headers.TryAddWithoutValidation("Origin", origin);
                }

                try
                {
                    response = await webSession.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    logger.LogWarning(
                        ex,
                        "Transient Steam web request error. host={Host} path={Path} attempt={Attempt}",
                        currentUri.Host,
                        currentUri.AbsolutePath,
                        attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            webSession.LastUri = response.RequestMessage?.RequestUri ?? currentUri;

            if (!allowRedirects || !IsRedirectStatusCode(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                return response;
            }

            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            response.Dispose();

            currentUri = nextUri;
            currentMethod = HttpMethod.Get;
            currentContent = null;
        }

        throw new TimeoutException("Too many redirects while calling Steam web endpoint.");
    }

    private static void ApplySessionCookies(CookieContainer cookieContainer, SteamSessionBundle bundle)
    {
        var loginValue = Uri.EscapeDataString($"{bundle.SteamId64}||{bundle.AccessToken}");
        var cookieValues = new[]
        {
            $"steamLoginSecure={loginValue}; path=/; secure",
            $"steamLogin={loginValue}; path=/; secure",
            $"sessionid={bundle.SessionId}; path=/; secure",
            "Steam_Language=english; path=/",
            "timezoneOffset=0,0; path=/"
        };

        foreach (var domain in new[] { "https://steamcommunity.com", "https://store.steampowered.com", "https://help.steampowered.com" })
        {
            var uri = new Uri(domain);
            foreach (var cookie in cookieValues)
            {
                cookieContainer.SetCookies(uri, cookie);
            }
        }
    }

    private async Task<HttpResponseMessage> SendCommunityFormAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        IReadOnlyDictionary<string, string> form,
        string mediaType,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        using HttpContent content = mediaType == MediaTypeNames.Application.FormUrlEncoded
            ? new FormUrlEncodedContent(form)
            : throw new InvalidOperationException($"Unsupported mediaType '{mediaType}'.");

        return await SendCommunityRequestAsync(
            method,
            url,
            bundle,
            content,
            allowRedirects,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCommunityRequestAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        SteamWebSession webSession,
        HttpContent? content,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        return await SendWebRequestAsync(
            webSession,
            method,
            url,
            content,
            allowRedirects,
            origin: "https://steamcommunity.com",
            referrer: "https://steamcommunity.com/my",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCommunityRequestAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        HttpContent? content,
        bool allowRedirects,
        CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirects,
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
        };

        var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(bundle));
        request.Headers.Referrer = new Uri("https://steamcommunity.com/my");

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        request.Dispose();
        client.Dispose();
        handler.Dispose();
        return response;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode code)
    {
        return code is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }

    private static string MapAuthenticationError(EResult result)
    {
        return result switch
        {
            EResult.InvalidPassword => "Invalid credentials.",
            EResult.InvalidLoginAuthCode => "Invalid email guard code.",
            EResult.TwoFactorCodeMismatch => "Invalid authenticator code.",
            EResult.AccountLoginDeniedNeedTwoFactor => "Two-factor authentication required.",
            EResult.AccountLogonDenied => "Steam Guard required.",
            EResult.RateLimitExceeded => "Steam rate limit exceeded. Try again later.",
            EResult.Timeout => "Steam timeout.",
            EResult.Expired => "Authentication session expired.",
            EResult.ServiceUnavailable => "Steam service unavailable.",
            _ => $"Steam authentication failed ({result})."
        };
    }

    private static bool TryParseBundle(string? payload, out SteamSessionBundle bundle, out string error)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            bundle = new SteamSessionBundle();
            error = "Session payload is empty.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SteamSessionBundle>(payload, JsonOptions);
            if (parsed is null)
            {
                bundle = new SteamSessionBundle();
                error = "Session payload is malformed.";
                return false;
            }

            HydrateBundleIdentityFromRawPayload(payload, parsed);
            if (string.IsNullOrWhiteSpace(parsed.SessionId))
            {
                parsed.SessionId = GenerateSessionId();
            }

            if (string.IsNullOrWhiteSpace(parsed.SteamId64) || string.IsNullOrWhiteSpace(parsed.AccessToken))
            {
                bundle = parsed;
                error = "Session payload misses required fields.";
                return false;
            }

            bundle = parsed;
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            bundle = new SteamSessionBundle();
            error = "Session payload is not valid JSON.";
            return false;
        }
    }

    private static void HydrateBundleIdentityFromRawPayload(string payload, SteamSessionBundle bundle)
    {
        if (!string.IsNullOrWhiteSpace(bundle.AccountName) && !string.IsNullOrWhiteSpace(bundle.LoginName))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(payload) as JsonObject;
            if (node is null)
            {
                return;
            }

            bundle.AccountName = CoalesceNonEmpty(
                bundle.AccountName,
                node["AccountName"]?.GetValue<string?>(),
                node["accountName"]?.GetValue<string?>(),
                node["account_name"]?.GetValue<string?>(),
                node["username"]?.GetValue<string?>(),
                node["login"]?.GetValue<string?>(),
                node["loginName"]?.GetValue<string?>());

            bundle.LoginName = CoalesceNonEmpty(
                bundle.LoginName,
                node["LoginName"]?.GetValue<string?>(),
                node["loginName"]?.GetValue<string?>(),
                node["username"]?.GetValue<string?>(),
                node["login"]?.GetValue<string?>(),
                node["accountName"]?.GetValue<string?>(),
                node["account_name"]?.GetValue<string?>());
        }
        catch (JsonException)
        {
            // ignored
        }
        catch (InvalidOperationException)
        {
            // ignored
        }
    }

    private static string CoalesceNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string SerializeBundle(SteamSessionBundle bundle)
    {
        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    private static DateTimeOffset? TryGetJwtExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);
            if (!doc.RootElement.TryGetProperty("exp", out var expElement))
            {
                return null;
            }

            if (!expElement.TryGetInt64(out var expUnix))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(expUnix);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetSteamIdFromJwt(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);

            var claims = new[] { "sub", "steamid", "steam_id" };
            foreach (var claim in claims)
            {
                if (!doc.RootElement.TryGetProperty(claim, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    var raw = value.GetString();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var normalized = raw!.Contains(':', StringComparison.Ordinal)
                        ? raw[(raw.LastIndexOf(':') + 1)..]
                        : raw;

                    if (ulong.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return parsed.ToString(CultureInfo.InvariantCulture);
                    }
                }
                else if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var asNumber))
                {
                    return asNumber.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static byte[] DecodeBase64Url(string input)
    {
        var normalized = input.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder != 0)
        {
            normalized += new string('=', 4 - remainder);
        }

        return Convert.FromBase64String(normalized);
    }

    private static JsonDocument ParseProfileEditConfig(string html)
    {
        var match = ProfileEditConfigRegex.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("Steam profile settings payload not found.");
        }

        var encoded = match.Groups["json"].Value;
        var decoded = WebUtility.HtmlDecode(encoded);

        return JsonDocument.Parse(decoded);
    }

    private static string? GetString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int GetInt32(JsonElement root, string property, int fallback)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return fallback;
    }

    private static string ResolveAvatarFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "avatar.png";
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "avatar.png";
        }

        extension = extension.ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "avatar.jpg",
            ".gif" => "avatar.gif",
            ".png" => "avatar.png",
            _ => "avatar.png"
        };
    }

    private static string ResolveAvatarContentType(string fileName)
    {
        var normalized = ResolveAvatarFileName(fileName);
        return normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? MediaTypeNames.Image.Jpeg :
            normalized.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? MediaTypeNames.Image.Gif :
            "image/png";
    }

    private static string BuildCookieHeader(SteamSessionBundle bundle)
    {
        var loginValue = Uri.EscapeDataString($"{bundle.SteamId64}||{bundle.AccessToken}");
        return string.Join("; ",
            $"steamLoginSecure={loginValue}",
            $"steamLogin={loginValue}",
            $"sessionid={bundle.SessionId}",
            "Steam_Language=english",
            "timezoneOffset=0,0");
    }

    private static string GenerateSessionId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    }

    private sealed record PasswordRecoveryContext(
        string SessionToken,
        string AccountId,
        string Reset,
        string Lost,
        string IssueId,
        string Method,
        string EnterCodeUrl);

    private sealed record PasswordChangeSubmitContext(
        string SessionToken,
        string AccountId,
        string LoginName);

    private sealed record HelpRsaKey(
        string ModulusHex,
        string ExponentHex,
        string Timestamp);

    private sealed record HtmlFormCandidate(
        string Action,
        string Method,
        IReadOnlyDictionary<string, string> HiddenFields);

    private sealed class SteamWebSession(
        SteamSessionBundle bundle,
        HttpClientHandler handler,
        HttpClient client) : IDisposable
    {
        public SteamSessionBundle Bundle { get; } = bundle;
        public HttpClientHandler Handler { get; } = handler;
        public HttpClient Client { get; } = client;
        public Uri? LastUri { get; set; }

        public void Dispose()
        {
            Client.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class SteamSessionBundle
    {
        public int Version { get; set; } = 2;
        public string SteamId64 { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string LoginName { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string? GuardData { get; set; }
        public string SessionId { get; set; } = GenerateSessionId();
        public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    private sealed class QrFlowState(
        Guid flowId,
        DateTimeOffset expiresAt,
        SteamClient steamClient,
        QrAuthSession authSession,
        CancellationTokenSource cancellation,
        Task callbackPumpTask,
        int pollingIntervalSeconds)
    {
        private readonly object _sync = new();
        private SteamQrAuthPollResult? _result;
        private string _challengeUrl = string.Empty;

        public Guid FlowId { get; } = flowId;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public SteamClient SteamClient { get; } = steamClient;
        public QrAuthSession AuthSession { get; } = authSession;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task CallbackPumpTask { get; } = callbackPumpTask;
        public int PollingIntervalSeconds { get; } = pollingIntervalSeconds;
        public Task MonitorTask { get; private set; } = Task.CompletedTask;

        public bool IsCanceled => Cancellation.IsCancellationRequested;

        public string ChallengeUrl
        {
            get
            {
                lock (_sync)
                {
                    return _challengeUrl;
                }
            }
        }

        public void StartMonitor(Func<Task> monitorFactory)
        {
            MonitorTask = Task.Run(monitorFactory, CancellationToken.None);
        }

        public void UpdateChallengeUrl(string challengeUrl)
        {
            if (string.IsNullOrWhiteSpace(challengeUrl))
            {
                return;
            }

            lock (_sync)
            {
                _challengeUrl = challengeUrl;
            }
        }

        public SteamQrAuthPollResult GetSnapshot()
        {
            lock (_sync)
            {
                if (_result is not null)
                {
                    return _result;
                }

                return new SteamQrAuthPollResult
                {
                    FlowId = FlowId,
                    Status = SteamQrAuthStatus.Pending,
                    ChallengeUrl = _challengeUrl,
                    ExpiresAt = ExpiresAt
                };
            }
        }

        public void MarkCompleted(SteamAuthResult authResult)
        {
            lock (_sync)
            {
                _result ??= new SteamQrAuthPollResult
                {
                    FlowId = FlowId,
                    Status = SteamQrAuthStatus.Completed,
                    ChallengeUrl = _challengeUrl,
                    ExpiresAt = ExpiresAt,
                    AuthResult = authResult
                };
            }
        }

        public void MarkFailed(string message)
        {
            lock (_sync)
            {
                _result ??= new SteamQrAuthPollResult
                {
                    FlowId = FlowId,
                    Status = SteamQrAuthStatus.Failed,
                    ChallengeUrl = _challengeUrl,
                    ExpiresAt = ExpiresAt,
                    ErrorMessage = message
                };
            }
        }

        public void MarkCanceled(string message)
        {
            lock (_sync)
            {
                _result ??= new SteamQrAuthPollResult
                {
                    FlowId = FlowId,
                    Status = SteamQrAuthStatus.Canceled,
                    ChallengeUrl = _challengeUrl,
                    ExpiresAt = ExpiresAt,
                    ErrorMessage = message
                };
            }

            Cancellation.Cancel();
        }

        public void MarkExpired(string message)
        {
            lock (_sync)
            {
                _result ??= new SteamQrAuthPollResult
                {
                    FlowId = FlowId,
                    Status = SteamQrAuthStatus.Expired,
                    ChallengeUrl = _challengeUrl,
                    ExpiresAt = ExpiresAt,
                    ErrorMessage = message
                };
            }

            Cancellation.Cancel();
        }

        public async Task StopRuntimeAsync()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch
            {
                // ignored
            }

            try
            {
                SteamClient.Disconnect();
            }
            catch
            {
                // ignored
            }

            try
            {
                await CallbackPumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch
            {
                // ignored
            }
        }
    }

    private sealed class PagePathResult
    {
        public bool Success { get; private init; }
        public string? Path { get; private init; }
        public string? ErrorMessage { get; private init; }

        public static PagePathResult FromPath(string path) => new() { Success = true, Path = path };
        public static PagePathResult Fail(string error) => new() { Success = false, ErrorMessage = error };
    }

    private static class SteamGuardCodeGenerator
    {
        private const string CodeChars = "23456789BCDFGHJKMNPQRTVWXY";

        public static string Generate(string sharedSecretBase64)
        {
            if (string.IsNullOrWhiteSpace(sharedSecretBase64))
            {
                throw new InvalidOperationException("Shared secret is empty.");
            }

            byte[] secret;
            try
            {
                secret = Convert.FromBase64String(sharedSecretBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Shared secret must be valid base64.", ex);
            }

            var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeSlice = unixTime / 30;
            var challenge = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(timeSlice));

            using var hmac = new HMACSHA1(secret);
            var digest = hmac.ComputeHash(challenge);
            var offset = digest[^1] & 0x0F;

            var codePoint =
                ((digest[offset] & 0x7F) << 24) |
                ((digest[offset + 1] & 0xFF) << 16) |
                ((digest[offset + 2] & 0xFF) << 8) |
                (digest[offset + 3] & 0xFF);

            Span<char> chars = stackalloc char[5];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = CodeChars[codePoint % CodeChars.Length];
                codePoint /= CodeChars.Length;
            }

            return new string(chars);
        }
    }

    private sealed class SteamCredentialAuthenticator(SteamCredentials credentials) : IAuthenticator
    {
        private readonly SteamCredentials _credentials = credentials;

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            return Task.FromResult(_credentials.AllowDeviceConfirmation);
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            if (!previousCodeWasIncorrect && !string.IsNullOrWhiteSpace(_credentials.GuardCode))
            {
                return Task.FromResult(_credentials.GuardCode!);
            }

            if (!string.IsNullOrWhiteSpace(_credentials.SharedSecret))
            {
                return Task.FromResult(SteamGuardCodeGenerator.Generate(_credentials.SharedSecret!));
            }

            throw new InvalidOperationException("Device code is required. Provide GuardCode or SharedSecret.");
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            if (!string.IsNullOrWhiteSpace(_credentials.GuardCode))
            {
                return Task.FromResult(_credentials.GuardCode!);
            }

            throw new InvalidOperationException($"Email code required for {email}. Provide GuardCode.");
        }
    }
}
