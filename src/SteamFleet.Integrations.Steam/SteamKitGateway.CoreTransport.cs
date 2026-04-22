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
using SteamFleet.Contracts.Steam;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
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

            var stableLogonId = ResolveStableLoginId(bundle);
            if (stableLogonId.HasValue)
            {
                details.LoginID = stableLogonId.Value;
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

    private async Task<T> ExecuteWithAccountFlowLockAsync<T>(
        SteamSessionBundle bundle,
        string flowName,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var steamId = string.IsNullOrWhiteSpace(bundle.SteamId64) ? "unknown" : bundle.SteamId64.Trim();
        var key = $"{steamId}:{flowName}";
        var flowLock = _accountFlowLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await flowLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            flowLock.Release();
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
                retryable = false;
                return SteamReasonCodes.InvalidCredentials;
            case EResult.Expired:
                retryable = true;
                return SteamReasonCodes.AuthSessionMissing;
            case EResult.AccessDenied:
                retryable = true;
                return SteamReasonCodes.AccessDenied;
            case EResult.LogonSessionReplaced:
                retryable = true;
                return SteamReasonCodes.SessionReplaced;
            case EResult.InvalidLoginAuthCode:
            case EResult.TwoFactorCodeMismatch:
            case EResult.AccountLogonDenied:
            case EResult.AccountLoginDeniedNeedTwoFactor:
                retryable = true;
                return SteamReasonCodes.GuardPending;
            case EResult.AccountLoginDeniedThrottle:
            case EResult.RateLimitExceeded:
                retryable = true;
                return SteamReasonCodes.AuthThrottled;
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

    private static uint? ResolveStableLoginId(SteamSessionBundle bundle)
    {
        if (ulong.TryParse(bundle.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId))
        {
            return (uint)(steamId & uint.MaxValue);
        }

        var seed = ResolveLogOnUsername(bundle);
        if (string.IsNullOrWhiteSpace(seed))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed.Trim()));
        return BitConverter.ToUInt32(bytes, 0);
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
            "mobileClient=android; path=/",
            "mobileClientVersion=777777 3.6.1; path=/",
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
            "mobileClient=android",
            "mobileClientVersion=777777 3.6.1",
            "Steam_Language=english",
            "timezoneOffset=0,0");
    }

    private static string GenerateSessionId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    }
}

