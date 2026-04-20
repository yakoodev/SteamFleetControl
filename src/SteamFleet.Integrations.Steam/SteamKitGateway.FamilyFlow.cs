using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamFleet.Contracts.Steam;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
    private static readonly Regex FamilyIdRegex = new(
        @"(?:[""'](?:family_?group_?id|familygroupid|family_?id|household_?id)[""']\s*[:=]\s*[""']?(?<id>[A-Za-z0-9_\-]{3,64})[""']?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SteamIdFieldRegex = new(
        @"(?:[""'](?:steamid64|steamid|steam_id64|steam_id)[""']\s*[:=]\s*[""']?(?<id>\d{16,20})[""']?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex FamilyRoleRegex = new(
        @"(?:[""'](?:self_?role|family_?role|member_?role|role)[""']\s*[:=]\s*[""'](?<role>[^""']{1,64})[""'])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex FamilyOrganizerRegex = new(
        @"(?:[""'](?:is_?organizer|is_?owner|owner)[""']\s*[:=]\s*(?<flag>true|false|1|0))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex DisplayNameRegex = new(
        @"(?:[""'](?:display_?name|persona_?name|name|account_?name)[""']\s*[:=]\s*[""'](?<value>[^""']{1,128})[""'])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex PersonaNameHtmlRegex = new(
        @"<span[^>]*class=([""'])actual_persona_name\1[^>]*>(?<name>.*?)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public async Task<SteamOperationResult> InviteToFamilyGroupAsync(
        string sessionPayload,
        string targetSteamId64,
        bool inviteAsChild = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return Failure(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (!ulong.TryParse(targetSteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var targetSteamId))
        {
            return Failure("Target SteamId64 is invalid.", SteamReasonCodes.TargetAccountMissing);
        }

        if (string.Equals(bundle.SteamId64, targetSteamId64, StringComparison.Ordinal))
        {
            return Failure("Нельзя отправить семейное приглашение самому себе.", SteamReasonCodes.EndpointRejected);
        }

        try
        {
            return await ExecuteWithAccountFlowLockAsync(
                    bundle,
                    flowName: "confirmations",
                    async flowToken =>
                    {
                        return await ExecuteWithLoggedOnSteamClientAsync(
                                bundle,
                                async (_, _, unified, _, _, token) =>
                                {
                                    var familyGroups = unified.CreateService<FamilyGroups>();
                                    var selfSteamId = ulong.Parse(bundle.SteamId64, CultureInfo.InvariantCulture);

                                    var userGroupResponse = await familyGroups.GetFamilyGroupForUser(new CFamilyGroups_GetFamilyGroupForUser_Request
                                        {
                                            steamid = selfSteamId,
                                            include_family_group_response = true
                                        })
                                        .ToTask()
                                        .WaitAsync(token)
                                        .ConfigureAwait(false);

                                    if (userGroupResponse.Result != EResult.OK)
                                    {
                                        return MapFamilyServiceFailure(
                                            "Steam не вернул семейную группу для аккаунта-организатора.",
                                            userGroupResponse.Result,
                                            null,
                                            defaultReasonCode: SteamReasonCodes.FamilyNotFound);
                                    }

                                    var familyGroupId = userGroupResponse.Body?.family_groupid ?? 0;
                                    if (familyGroupId == 0 || userGroupResponse.Body?.is_not_member_of_any_group == true)
                                    {
                                        return Failure(
                                            "Аккаунт не состоит в Steam Family. Сначала создайте/вступите в семью.",
                                            SteamReasonCodes.FamilyNotFound);
                                    }

                                    var role = inviteAsChild
                                        ? EFamilyGroupRole.k_EFamilyGroupRole_Child
                                        : EFamilyGroupRole.k_EFamilyGroupRole_Adult;

                                    var inviteResponse = await familyGroups.InviteToFamilyGroup(new CFamilyGroups_InviteToFamilyGroup_Request
                                        {
                                            family_groupid = familyGroupId,
                                            receiver_steamid = targetSteamId,
                                            receiver_role = role
                                        })
                                        .ToTask()
                                        .WaitAsync(token)
                                        .ConfigureAwait(false);

                                    if (inviteResponse.Result != EResult.OK)
                                    {
                                        return MapFamilyServiceFailure(
                                            "Steam отклонил отправку семейного приглашения.",
                                            inviteResponse.Result,
                                            familyGroupId,
                                            defaultReasonCode: SteamReasonCodes.EndpointRejected);
                                    }

                                    var twoFactor = inviteResponse.Body?.two_factor_method ?? EFamilyGroupsTwoFactorMethod.k_EFamilyGroupsTwoFactorMethodNone;
                                    var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["familyGroupId"] = familyGroupId.ToString(CultureInfo.InvariantCulture),
                                        ["targetSteamId64"] = targetSteamId64,
                                        ["inviteRole"] = inviteAsChild ? "Child" : "Adult",
                                        ["twoFactorMethod"] = twoFactor.ToString(),
                                        ["inviteId"] = (inviteResponse.Body?.invite_id ?? 0).ToString(CultureInfo.InvariantCulture)
                                    };

                                    if (twoFactor != EFamilyGroupsTwoFactorMethod.k_EFamilyGroupsTwoFactorMethodNone)
                                    {
                                        return Failure(
                                            $"Steam требует 2FA-подтверждение отправки инвайта ({ToFriendlyTwoFactorMethod(twoFactor)}).",
                                            SteamReasonCodes.GuardPending,
                                            retryable: true,
                                            data);
                                    }

                                    return new SteamOperationResult
                                    {
                                        Success = true,
                                        ReasonCode = SteamReasonCodes.None,
                                        Data = data
                                    };
                                },
                                flowToken)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SteamGatewayOperationException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to send Steam family invite from steamId={SteamId} to target={TargetSteamId}; reason={ReasonCode}",
                bundle.SteamId64,
                targetSteamId64,
                ex.ReasonCode);
            return Failure(ex.Message, ex.ReasonCode ?? SteamReasonCodes.Unknown, ex.Retryable);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Steam family invite flow timed out for steamId={SteamId}", bundle.SteamId64);
            return Failure("Отправка семейного приглашения превысила таймаут.", SteamReasonCodes.Timeout, retryable: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Steam family invite flow failed for steamId={SteamId}", bundle.SteamId64);
            return Failure("Не удалось отправить семейное приглашение Steam.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    public async Task<SteamOperationResult> AcceptFamilyInviteAsync(
        string sessionPayload,
        string? sourceSteamId64 = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return Failure(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        ulong? sourceSteamId = null;
        if (!string.IsNullOrWhiteSpace(sourceSteamId64))
        {
            if (!ulong.TryParse(sourceSteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSource))
            {
                return Failure("sourceSteamId64 имеет некорректный формат.", SteamReasonCodes.SourceAccountMissing);
            }

            sourceSteamId = parsedSource;
        }

        try
        {
            return await ExecuteWithAccountFlowLockAsync(
                    bundle,
                    flowName: "confirmations",
                    async flowToken =>
                    {
                        return await ExecuteWithLoggedOnSteamClientAsync(
                                bundle,
                                async (_, _, unified, _, _, token) =>
                                {
                                    var familyGroups = unified.CreateService<FamilyGroups>();
                                    var selfSteamId = ulong.Parse(bundle.SteamId64, CultureInfo.InvariantCulture);

                                    var userGroupResponse = await familyGroups.GetFamilyGroupForUser(new CFamilyGroups_GetFamilyGroupForUser_Request
                                        {
                                            steamid = selfSteamId,
                                            include_family_group_response = true
                                        })
                                        .ToTask()
                                        .WaitAsync(token)
                                        .ConfigureAwait(false);

                                    if (userGroupResponse.Result != EResult.OK)
                                    {
                                        return MapFamilyServiceFailure(
                                            "Steam не вернул список входящих семейных приглашений.",
                                            userGroupResponse.Result,
                                            null,
                                            defaultReasonCode: SteamReasonCodes.FamilyNotFound);
                                    }

                                    var pending = userGroupResponse.Body?.pending_group_invites ?? [];
                                    var invite = sourceSteamId.HasValue
                                        ? pending.FirstOrDefault(x => x.inviter_steamid == sourceSteamId.Value)
                                        : pending.FirstOrDefault();

                                    if (invite is null || invite.family_groupid == 0)
                                    {
                                        var suffix = sourceSteamId.HasValue
                                            ? $" от источника {sourceSteamId.Value.ToString(CultureInfo.InvariantCulture)}"
                                            : string.Empty;
                                        return Failure(
                                            $"Активное семейное приглашение{suffix} не найдено.",
                                            SteamReasonCodes.FamilyNotFound);
                                    }

                                    if (invite.awaiting_2fa)
                                    {
                                        return Failure(
                                            "Steam ожидает 2FA-подтверждение приглашения. Завершите подтверждение в Steam Guard и повторите.",
                                            SteamReasonCodes.GuardPending,
                                            retryable: true,
                                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                            {
                                                ["familyGroupId"] = invite.family_groupid.ToString(CultureInfo.InvariantCulture),
                                                ["inviterSteamId64"] = invite.inviter_steamid.ToString(CultureInfo.InvariantCulture)
                                            });
                                    }

                                    var joinResponse = await familyGroups.JoinFamilyGroup(new CFamilyGroups_JoinFamilyGroup_Request
                                        {
                                            family_groupid = invite.family_groupid,
                                            nonce = 0
                                        })
                                        .ToTask()
                                        .WaitAsync(token)
                                        .ConfigureAwait(false);

                                    if (joinResponse.Result != EResult.OK)
                                    {
                                        return MapFamilyServiceFailure(
                                            "Steam отклонил принятие семейного приглашения.",
                                            joinResponse.Result,
                                            invite.family_groupid,
                                            defaultReasonCode: SteamReasonCodes.EndpointRejected);
                                    }

                                    var twoFactor = joinResponse.Body?.two_factor_method ?? EFamilyGroupsTwoFactorMethod.k_EFamilyGroupsTwoFactorMethodNone;
                                    var inviteAlreadyAccepted = joinResponse.Body?.invite_already_accepted == true;
                                    var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["familyGroupId"] = invite.family_groupid.ToString(CultureInfo.InvariantCulture),
                                        ["inviterSteamId64"] = invite.inviter_steamid.ToString(CultureInfo.InvariantCulture),
                                        ["twoFactorMethod"] = twoFactor.ToString(),
                                        ["inviteAlreadyAccepted"] = inviteAlreadyAccepted.ToString()
                                    };

                                    if (twoFactor != EFamilyGroupsTwoFactorMethod.k_EFamilyGroupsTwoFactorMethodNone && !inviteAlreadyAccepted)
                                    {
                                        return Failure(
                                            $"Steam требует 2FA-подтверждение принятия инвайта ({ToFriendlyTwoFactorMethod(twoFactor)}).",
                                            SteamReasonCodes.GuardPending,
                                            retryable: true,
                                            data);
                                    }

                                    return new SteamOperationResult
                                    {
                                        Success = true,
                                        ReasonCode = SteamReasonCodes.None,
                                        Data = data
                                    };
                                },
                                flowToken)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SteamGatewayOperationException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to accept Steam family invite for steamId={SteamId}; source={SourceSteamId}; reason={ReasonCode}",
                bundle.SteamId64,
                sourceSteamId64,
                ex.ReasonCode);
            return Failure(ex.Message, ex.ReasonCode ?? SteamReasonCodes.Unknown, ex.Retryable);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Steam family invite accept flow timed out for steamId={SteamId}", bundle.SteamId64);
            return Failure("Принятие семейного приглашения превысило таймаут.", SteamReasonCodes.Timeout, retryable: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Steam family invite accept flow failed for steamId={SteamId}", bundle.SteamId64);
            return Failure("Не удалось принять семейное приглашение Steam.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    public async Task<SteamFamilySnapshot> GetFamilySnapshotAsync(
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
            using var webSession = CreateWebSession(bundle);
            using var response = await SendStoreRequestAsync(
                    HttpMethod.Get,
                    "https://store.steampowered.com/account/familymanagement?l=english",
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
                "Steam family flow attempt. flow={Flow} host={Host} path={Path} status={StatusCode}",
                "store_family_management",
                responseUri?.Host,
                responsePath,
                (int)response.StatusCode);

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                responsePath.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                LooksLikeStoreLoginPage(body) ||
                LooksLikeStoreLoginFeatureTarget(body))
            {
                throw new SteamGatewayOperationException(
                    "Steam store session is unauthorized for family sync.",
                    SteamReasonCodes.AuthSessionMissing,
                    retryable: true);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ||
                LooksLikeAntiBot(body))
            {
                throw new SteamGatewayOperationException(
                    "Steam blocked family management flow.",
                    SteamReasonCodes.AntiBotBlocked,
                    retryable: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SteamGatewayOperationException(
                    $"Steam family management page failed: HTTP {(int)response.StatusCode}.",
                    SteamReasonCodes.FamilySyncFailed,
                    retryable: (int)response.StatusCode >= 500);
            }

            if (TryParseFamilySnapshotFromHtml(body, bundle.SteamId64, out var snapshot))
            {
                snapshot.SyncedAt = DateTimeOffset.UtcNow;
                return snapshot;
            }

            if (LooksLikeNoFamilyMembershipPage(body))
            {
                return new SteamFamilySnapshot
                {
                    FamilyId = null,
                    SelfRole = null,
                    IsOrganizer = false,
                    SyncedAt = DateTimeOffset.UtcNow,
                    Members = []
                };
            }

            throw new SteamGatewayOperationException(
                "Steam family data is unavailable on family management page.",
                SteamReasonCodes.FamilyNotFound,
                retryable: false);
        }
        catch (SteamGatewayOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync Steam family for steamId={SteamId}", bundle.SteamId64);
            throw new SteamGatewayOperationException(
                "Could not sync Steam family data.",
                SteamReasonCodes.FamilySyncFailed,
                retryable: true,
                ex);
        }
    }

    public async Task<SteamPublicMemberData> GetPublicMemberDataAsync(
        string steamId64,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(steamId64) ||
            !ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new SteamGatewayOperationException(
                "SteamId64 is invalid.",
                SteamReasonCodes.ExternalDataUnavailable,
                retryable: false);
        }

        var profileUrl = $"https://steamcommunity.com/profiles/{steamId64}";
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

            var profileHtml = await SendPublicGetAsync(client, profileUrl, cancellationToken).ConfigureAwait(false);
            var resolvedProfileUrl = TryReadProfileUrlFromHtml(profileHtml) ?? profileUrl;
            var displayName = TryExtractPersonaName(profileHtml);
            var isPublic = !LooksLikePrivateProfilePage(profileHtml);

            var games = new List<SteamOwnedGame>();
            if (isPublic)
            {
                var gamesHtml = await SendPublicGetAsync(
                        client,
                        $"{resolvedProfileUrl.TrimEnd('/')}/games/?tab=all",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!LooksLikePrivateProfilePage(gamesHtml) && !LooksLikeCommunityLoginPage(gamesHtml))
                {
                    if (TryParseSsrRenderContext(gamesHtml, out var renderContext))
                    {
                        using (renderContext)
                        {
                            CollectOwnedGames(renderContext.RootElement, games);
                        }
                    }

                    if (games.Count == 0)
                    {
                        CollectOwnedGamesFromRawHtml(gamesHtml, games);
                    }
                }
                else
                {
                    isPublic = false;
                }
            }

            var dedup = games
                .GroupBy(x => x.AppId)
                .Select(group => group.OrderByDescending(x => x.PlaytimeMinutes).First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new SteamPublicMemberData
            {
                SteamId64 = steamId64,
                DisplayName = displayName,
                ProfileUrl = resolvedProfileUrl,
                IsPublic = isPublic,
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
            logger.LogDebug(ex, "Failed to fetch external Steam public data for steamId={SteamId}", steamId64);
            throw new SteamGatewayOperationException(
                "Could not fetch external Steam public data.",
                SteamReasonCodes.ExternalDataUnavailable,
                retryable: true,
                ex);
        }
    }

    private static bool TryParseFamilySnapshotFromHtml(
        string html,
        string selfSteamId64,
        out SteamFamilySnapshot snapshot)
    {
        snapshot = new SteamFamilySnapshot
        {
            SyncedAt = DateTimeOffset.UtcNow
        };

        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var familyId = FamilyIdRegex.Match(html).Success
            ? FamilyIdRegex.Match(html).Groups["id"].Value.Trim()
            : null;

        var members = new Dictionary<string, SteamFamilyMember>(StringComparer.Ordinal);

        foreach (Match steamIdMatch in SteamIdFieldRegex.Matches(html))
        {
            if (!steamIdMatch.Success)
            {
                continue;
            }

            var steamId = steamIdMatch.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(steamId))
            {
                continue;
            }

            var segmentStart = Math.Max(0, steamIdMatch.Index - 260);
            var segmentLength = Math.Min(720, html.Length - segmentStart);
            var segment = html.Substring(segmentStart, segmentLength);

            var displayName = MatchValue(DisplayNameRegex, segment);
            var role = MatchValue(FamilyRoleRegex, segment);
            var isOrganizer = MatchFlag(FamilyOrganizerRegex, segment);

            if (!members.TryGetValue(steamId, out var member))
            {
                member = new SteamFamilyMember
                {
                    SteamId64 = steamId
                };
                members[steamId] = member;
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                member.DisplayName = displayName;
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                member.Role = role;
            }

            if (isOrganizer)
            {
                member.IsOrganizer = true;
            }
        }

        if (members.Count == 0)
        {
            return !string.IsNullOrWhiteSpace(familyId);
        }

        if (!string.IsNullOrWhiteSpace(selfSteamId64) &&
            members.TryGetValue(selfSteamId64, out var self))
        {
            snapshot.SelfRole = self.Role;
            snapshot.IsOrganizer = self.IsOrganizer;
        }

        snapshot.FamilyId = familyId;
        snapshot.Members = members.Values
            .OrderBy(x => x.DisplayName ?? x.SteamId64, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
    }

    private async Task<string> SendPublicGetAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("*/*");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || LooksLikeAntiBot(body))
        {
            throw new SteamGatewayOperationException(
                "Steam blocked public profile data request.",
                SteamReasonCodes.AntiBotBlocked,
                retryable: true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SteamGatewayOperationException(
                "Steam profile was not found.",
                SteamReasonCodes.ExternalDataUnavailable,
                retryable: false);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SteamGatewayOperationException(
                $"Steam public endpoint failed: HTTP {(int)response.StatusCode}.",
                SteamReasonCodes.ExternalDataUnavailable,
                retryable: (int)response.StatusCode >= 500);
        }

        return body;
    }

    private static bool LooksLikeStoreLoginFeatureTarget(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return html.Contains("data-featuretarget=\"login\"", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("login_featuretarget_ctn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNoFamilyMembershipPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var normalized = html.ToLowerInvariant();
        return normalized.Contains("create a steam family", StringComparison.Ordinal) ||
               normalized.Contains("join a steam family", StringComparison.Ordinal) ||
               normalized.Contains("you're not in a steam family", StringComparison.Ordinal) ||
               normalized.Contains("you are not in a steam family", StringComparison.Ordinal) ||
               normalized.Contains("not currently in a steam family", StringComparison.Ordinal);
    }

    private static bool LooksLikePrivateProfilePage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var normalized = html.ToLowerInvariant();
        return normalized.Contains("this profile is private", StringComparison.Ordinal) ||
               normalized.Contains("profile is private", StringComparison.Ordinal) ||
               normalized.Contains("friends-only", StringComparison.Ordinal) ||
               normalized.Contains("games list is private", StringComparison.Ordinal);
    }

    private static string? MatchValue(Regex regex, string text)
    {
        var match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = WebUtility.HtmlDecode(match.Groups["value"].Success
            ? match.Groups["value"].Value
            : match.Groups["role"].Success
                ? match.Groups["role"].Value
                : string.Empty);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool MatchFlag(Regex regex, string text)
    {
        var match = regex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var raw = match.Groups["flag"].Value.Trim();
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
    }

    private static string? TryExtractPersonaName(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = PersonaNameHtmlRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var value = WebUtility.HtmlDecode(match.Groups["name"].Value);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static SteamOperationResult MapFamilyServiceFailure(
        string message,
        EResult result,
        ulong? familyGroupId,
        string? defaultReasonCode = null)
    {
        var reasonCode = MapFamilyResultReason(result, out var retryable);
        if (!string.IsNullOrWhiteSpace(defaultReasonCode) && reasonCode == SteamReasonCodes.Unknown)
        {
            reasonCode = defaultReasonCode;
        }

        return Failure(
            $"{message} ({result}).",
            reasonCode,
            retryable,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["result"] = result.ToString(),
                ["familyGroupId"] = familyGroupId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            });
    }

    private static string MapFamilyResultReason(EResult result, out bool retryable)
    {
        switch (result)
        {
            case EResult.OK:
                retryable = false;
                return SteamReasonCodes.None;
            case EResult.InvalidPassword:
                retryable = false;
                return SteamReasonCodes.InvalidCredentials;
            case EResult.Expired:
            case EResult.NoConnection:
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
            case EResult.Busy:
            case EResult.TryAnotherCM:
            case EResult.RemoteCallFailed:
                retryable = true;
                return SteamReasonCodes.Timeout;
            case EResult.InvalidParam:
            case EResult.InvalidState:
                retryable = false;
                return SteamReasonCodes.EndpointRejected;
            case EResult.AccountNotFound:
                retryable = false;
                return SteamReasonCodes.TargetAccountMissing;
            default:
                retryable = false;
                return SteamReasonCodes.Unknown;
        }
    }

    private static string ToFriendlyTwoFactorMethod(EFamilyGroupsTwoFactorMethod method)
    {
        return method switch
        {
            EFamilyGroupsTwoFactorMethod.k_EFamilyGroupsTwoFactorMethodMobile => "mobile",
            EFamilyGroupsTwoFactorMethod.k_EFamilyGroupsTwoFactorMethodEmail => "email",
            _ => "none"
        };
    }
}
