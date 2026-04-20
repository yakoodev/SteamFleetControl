using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamFleet.Contracts.Steam;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
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
}

