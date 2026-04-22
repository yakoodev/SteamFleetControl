using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SteamFleet.Contracts.Steam;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
    private static readonly object GuardTimeSyncLock = new();
    private static DateTimeOffset _guardTimeSyncAtUtc = DateTimeOffset.MinValue;
    private static long _guardTimeOffsetSeconds;

    public async Task<SteamGuardConfirmationsResult> GetConfirmationsAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return new SteamGuardConfirmationsResult
            {
                Success = false,
                ErrorMessage = parseError,
                ReasonCode = SteamReasonCodes.AuthSessionMissing,
                Retryable = true
            };
        }

        if (string.IsNullOrWhiteSpace(identitySecret) || string.IsNullOrWhiteSpace(deviceId))
        {
            return new SteamGuardConfirmationsResult
            {
                Success = false,
                ErrorMessage = "Identity secret and device id are required for mobile confirmations.",
                ReasonCode = SteamReasonCodes.GuardNotConfigured
            };
        }

        try
        {
            var time = await GetAlignedSteamUnixTimeAsync(cancellationToken).ConfigureAwait(false);
            var query = BuildConfirmationQueryString(bundle.SteamId64, identitySecret, deviceId, time, "conf");
            var url = $"https://steamcommunity.com/mobileconf/getlist?{query}";
            var response = await SendMobileCommunityRequestAsync(
                    HttpMethod.Get,
                    url,
                    bundle,
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.RequestUri?.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new SteamGuardConfirmationsResult
                {
                    Success = false,
                    ErrorMessage = "Steam mobile confirmations require active login.",
                    ReasonCode = SteamReasonCodes.AuthSessionMissing,
                    Retryable = true
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new SteamGuardConfirmationsResult
                {
                    Success = false,
                    ErrorMessage = $"Steam mobile confirmations failed: HTTP {(int)response.StatusCode}.",
                    ReasonCode = SteamReasonCodes.EndpointRejected,
                    Retryable = (int)response.StatusCode >= 500
                };
            }

            using var json = JsonDocument.Parse(response.Body);
            var root = json.RootElement;

            var result = new SteamGuardConfirmationsResult
            {
                Success = true,
                SyncedAt = DateTimeOffset.UtcNow,
                NeedAuthentication = root.TryGetProperty("needauth", out var needAuth) && ReadBoolean(needAuth)
            };

            if (!TryReadSuccessFlag(root))
            {
                result.Success = false;
                result.ErrorMessage = GetJsonString(root, "message") ?? "Steam did not confirm mobile confirmations request.";
                result.ReasonCode = result.NeedAuthentication
                    ? SteamReasonCodes.AuthSessionMissing
                    : SteamReasonCodes.EndpointRejected;
                result.Retryable = result.NeedAuthentication;
                return result;
            }

            if (root.TryGetProperty("conf", out var confNode) && confNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in confNode.EnumerateArray())
                {
                    var summary = new List<string>();
                    if (item.TryGetProperty("summary", out var summaryNode) && summaryNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var line in summaryNode.EnumerateArray())
                        {
                            if (line.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var text = line.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                summary.Add(text.Trim());
                            }
                        }
                    }

                    result.Confirmations.Add(new SteamGuardConfirmation
                    {
                        Id = ReadUInt64(item, "id"),
                        Key = ReadUInt64(item, "nonce"),
                        CreatorId = ReadUInt64(item, "creator_id"),
                        Headline = GetJsonString(item, "headline"),
                        Summary = summary,
                        AcceptText = GetJsonString(item, "accept"),
                        CancelText = GetJsonString(item, "cancel"),
                        IconUrl = GetJsonString(item, "icon"),
                        Type = ParseConfirmationType(item)
                    });
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Steam mobile confirmations");
            return new SteamGuardConfirmationsResult
            {
                Success = false,
                ErrorMessage = "Failed to fetch Steam mobile confirmations.",
                ReasonCode = SteamReasonCodes.Unknown,
                Retryable = true
            };
        }
    }

    public Task<SteamOperationResult> AcceptConfirmationAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        ulong confirmationId,
        ulong confirmationKey,
        CancellationToken cancellationToken = default)
        => SendConfirmationOpAsync(
            sessionPayload,
            identitySecret,
            deviceId,
            confirmationId,
            confirmationKey,
            operation: "allow",
            tag: "accept",
            cancellationToken);

    public Task<SteamOperationResult> DenyConfirmationAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        ulong confirmationId,
        ulong confirmationKey,
        CancellationToken cancellationToken = default)
        => SendConfirmationOpAsync(
            sessionPayload,
            identitySecret,
            deviceId,
            confirmationId,
            confirmationKey,
            operation: "cancel",
            tag: "reject",
            cancellationToken);

    public async Task<SteamOperationResult> AcceptConfirmationsBatchAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        IReadOnlyCollection<SteamGuardConfirmationRef> confirmations,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return Failure(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (string.IsNullOrWhiteSpace(identitySecret) || string.IsNullOrWhiteSpace(deviceId))
        {
            return Failure(
                "Identity secret and device id are required for mobile confirmations.",
                SteamReasonCodes.GuardNotConfigured);
        }

        if (confirmations.Count == 0)
        {
            return Failure("At least one confirmation is required.", SteamReasonCodes.EndpointRejected);
        }

        try
        {
            var time = await GetAlignedSteamUnixTimeAsync(cancellationToken).ConfigureAwait(false);
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["op"] = "allow"
            };

            foreach (var part in ParseQueryString(BuildConfirmationQueryString(bundle.SteamId64, identitySecret, deviceId, time, "accept")))
            {
                form[part.Key] = part.Value;
            }

            var index = 0;
            foreach (var confirmation in confirmations)
            {
                form[$"cid[{index}]"] = confirmation.Id.ToString();
                form[$"ck[{index}]"] = confirmation.Key.ToString();
                index++;
            }

            using var content = new FormUrlEncodedContent(form);
            var response = await SendMobileCommunityRequestAsync(
                    HttpMethod.Post,
                    "https://steamcommunity.com/mobileconf/multiajaxop",
                    bundle,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);

            return ParseConfirmationOperationResponse(response, SteamReasonCodes.EndpointRejected);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to accept Steam mobile confirmations batch");
            return Failure("Failed to accept Steam mobile confirmations batch.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    public async Task<SteamGuardLinkState> StartAuthenticatorLinkAsync(
        string sessionPayload,
        string? phoneNumber = null,
        string? phoneCountryCode = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return FailLink(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        try
        {
            var deviceId = GenerateAndroidDeviceId();
            var time = await GetAlignedSteamUnixTimeAsync(cancellationToken).ConfigureAwait(false);

            var addResponse = await PostSteamApiFormAsync(
                    $"https://api.steampowered.com/ITwoFactorService/AddAuthenticator/v1/?access_token={Uri.EscapeDataString(bundle.AccessToken)}",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["steamid"] = bundle.SteamId64,
                        ["authenticator_time"] = time.ToString(),
                        ["authenticator_type"] = "1",
                        ["device_identifier"] = deviceId,
                        ["sms_phone_id"] = "1"
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!addResponse.Success)
            {
                return FailLink(
                    $"Steam AddAuthenticator failed: HTTP {(int)addResponse.StatusCode}.",
                    SteamReasonCodes.EndpointRejected,
                    retryable: (int)addResponse.StatusCode >= 500);
            }

            using var addJson = JsonDocument.Parse(addResponse.Body);
            var addRoot = addJson.RootElement;
            if (!addRoot.TryGetProperty("response", out var responseNode) || responseNode.ValueKind != JsonValueKind.Object)
            {
                return FailLink("Steam AddAuthenticator returned malformed JSON.", SteamReasonCodes.Unknown, retryable: true);
            }

            var status = ReadInt(responseNode, "status", -1);
            if (status == 2)
            {
                if (string.IsNullOrWhiteSpace(phoneNumber))
                {
                    return new SteamGuardLinkState
                    {
                        Success = true,
                        Step = SteamGuardLinkStep.NeedPhoneNumber,
                        ReasonCode = SteamReasonCodes.GuardLinkPending
                    };
                }

                var phoneState = await ProvidePhoneForLinkAsync(sessionPayload, phoneNumber, phoneCountryCode, cancellationToken)
                    .ConfigureAwait(false);
                return phoneState;
            }

            if (status == 29)
            {
                return FailLink(
                    "Steam reports that an authenticator is already linked to this account.",
                    SteamReasonCodes.EndpointRejected,
                    retryable: false);
            }

            if (status != 1)
            {
                return FailLink(
                    $"Steam AddAuthenticator returned status={status}.",
                    SteamReasonCodes.EndpointRejected,
                    retryable: false);
            }

            var linkState = new SteamGuardLinkState
            {
                Success = true,
                Step = SteamGuardLinkStep.NeedSmsCode,
                ReasonCode = SteamReasonCodes.GuardLinkPending,
                Retryable = true,
                DeviceId = deviceId,
                SharedSecret = GetJsonString(responseNode, "shared_secret"),
                IdentitySecret = GetJsonString(responseNode, "identity_secret"),
                RevocationCode = GetJsonString(responseNode, "revocation_code"),
                SerialNumber = GetJsonString(responseNode, "serial_number"),
                TokenGid = GetJsonString(responseNode, "token_gid"),
                Uri = GetJsonString(responseNode, "uri"),
                FullyEnrolled = ReadBoolean(responseNode, "fully_enrolled")
            };

            if (JsonNode.Parse(responseNode.GetRawText()) is JsonObject payloadNode)
            {
                payloadNode["device_id"] = deviceId;
                payloadNode["fully_enrolled"] = linkState.FullyEnrolled;
                linkState.RecoveryPayload = payloadNode.ToJsonString(JsonOptions);
            }
            else
            {
                linkState.RecoveryPayload = responseNode.GetRawText();
            }

            return linkState;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start Steam authenticator linking");
            return FailLink("Failed to start authenticator linking.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    public async Task<SteamGuardLinkState> ProvidePhoneForLinkAsync(
        string sessionPayload,
        string phoneNumber,
        string? phoneCountryCode = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return FailLink(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return FailLink("Phone number is required.", SteamReasonCodes.EndpointRejected);
        }

        try
        {
            var response = await PostSteamApiFormAsync(
                    $"https://api.steampowered.com/IPhoneService/SetAccountPhoneNumber/v1/?access_token={Uri.EscapeDataString(bundle.AccessToken)}",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["phone_number"] = phoneNumber.Trim(),
                        ["phone_country_code"] = string.IsNullOrWhiteSpace(phoneCountryCode) ? "7" : phoneCountryCode.Trim()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.Success)
            {
                return FailLink(
                    $"Steam SetAccountPhoneNumber failed: HTTP {(int)response.StatusCode}.",
                    SteamReasonCodes.EndpointRejected,
                    retryable: (int)response.StatusCode >= 500);
            }

            using var json = JsonDocument.Parse(response.Body);
            if (!json.RootElement.TryGetProperty("response", out var root))
            {
                return FailLink("Steam phone setup returned malformed JSON.", SteamReasonCodes.Unknown, retryable: true);
            }

            var email = GetJsonString(root, "confirmation_email_address");
            return new SteamGuardLinkState
            {
                Success = true,
                Step = SteamGuardLinkStep.NeedEmailConfirmation,
                ReasonCode = SteamReasonCodes.GuardLinkPending,
                Retryable = true,
                PhoneNumberHint = GetJsonString(root, "phone_number_formatted") ?? phoneNumber.Trim(),
                ConfirmationEmailAddress = email
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to provide phone for Steam authenticator linking");
            return FailLink("Failed to set account phone number for linking.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    public async Task<SteamGuardLinkState> FinalizeAuthenticatorLinkAsync(
        string sessionPayload,
        string sharedSecret,
        string smsCode,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return FailLink(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (string.IsNullOrWhiteSpace(sharedSecret) || string.IsNullOrWhiteSpace(smsCode))
        {
            return FailLink("Shared secret and SMS code are required.", SteamReasonCodes.EndpointRejected);
        }

        try
        {
            for (var attempt = 0; attempt <= 10; attempt++)
            {
                var time = await GetAlignedSteamUnixTimeAsync(cancellationToken).ConfigureAwait(false);
                var code = await SteamGuardCodeGenerator.GenerateAsync(
                        sharedSecret.Trim(),
                        forceResync: attempt > 0)
                    .ConfigureAwait(false);

                var response = await PostSteamApiFormAsync(
                        $"https://api.steampowered.com/ITwoFactorService/FinalizeAddAuthenticator/v1/?access_token={Uri.EscapeDataString(bundle.AccessToken)}",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["steamid"] = bundle.SteamId64,
                            ["authenticator_code"] = code,
                            ["authenticator_time"] = time.ToString(),
                            ["activation_code"] = smsCode.Trim(),
                            ["validate_sms_code"] = "1"
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!response.Success)
                {
                    return FailLink(
                        $"Steam FinalizeAddAuthenticator failed: HTTP {(int)response.StatusCode}.",
                        SteamReasonCodes.EndpointRejected,
                        retryable: (int)response.StatusCode >= 500);
                }

                using var json = JsonDocument.Parse(response.Body);
                if (!json.RootElement.TryGetProperty("response", out var node))
                {
                    return FailLink("Steam finalization returned malformed JSON.", SteamReasonCodes.Unknown, retryable: true);
                }

                var status = ReadInt(node, "status", -1);
                if (status == 89)
                {
                    return FailLink(
                        "Steam rejected SMS code.",
                        SteamReasonCodes.GuardPending,
                        retryable: true);
                }

                if (!ReadBoolean(node, "success"))
                {
                    if (status == 88 && attempt < 10)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return FailLink(
                        $"Steam finalization failed (status={status}).",
                        SteamReasonCodes.EndpointRejected,
                        retryable: status == 88);
                }

                if (ReadBoolean(node, "want_more"))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return new SteamGuardLinkState
                {
                    Success = true,
                    Step = SteamGuardLinkStep.Completed,
                    FullyEnrolled = true,
                    ReasonCode = SteamReasonCodes.None
                };
            }

            return FailLink(
                "Steam finalization required too many retries.",
                SteamReasonCodes.Timeout,
                retryable: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to finalize Steam authenticator linking");
            return FailLink("Failed to finalize authenticator linking.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    public async Task<SteamOperationResult> RemoveAuthenticatorAsync(
        string sessionPayload,
        string revocationCode,
        int scheme = 1,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return Failure(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (string.IsNullOrWhiteSpace(revocationCode))
        {
            return Failure("Revocation code is required.", SteamReasonCodes.EndpointRejected);
        }

        try
        {
            var response = await PostSteamApiFormAsync(
                    $"https://api.steampowered.com/ITwoFactorService/RemoveAuthenticator/v1/?access_token={Uri.EscapeDataString(bundle.AccessToken)}",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["revocation_code"] = revocationCode.Trim(),
                        ["revocation_reason"] = "1",
                        ["steamguard_scheme"] = Math.Clamp(scheme, 1, 2).ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.Success)
            {
                return Failure(
                    $"Steam RemoveAuthenticator failed: HTTP {(int)response.StatusCode}.",
                    SteamReasonCodes.EndpointRejected,
                    retryable: (int)response.StatusCode >= 500);
            }

            using var json = JsonDocument.Parse(response.Body);
            if (!json.RootElement.TryGetProperty("response", out var root))
            {
                return Failure("Steam RemoveAuthenticator returned malformed JSON.", SteamReasonCodes.Unknown, retryable: true);
            }

            if (!ReadBoolean(root, "success"))
            {
                return Failure("Steam rejected authenticator removal request.", SteamReasonCodes.EndpointRejected);
            }

            return new SteamOperationResult
            {
                Success = true,
                ReasonCode = SteamReasonCodes.None,
                Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scheme"] = Math.Clamp(scheme, 1, 2).ToString(),
                    ["removed"] = true.ToString()
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove Steam authenticator");
            return Failure("Failed to remove authenticator.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    private async Task<SteamOperationResult> SendConfirmationOpAsync(
        string sessionPayload,
        string identitySecret,
        string deviceId,
        ulong confirmationId,
        ulong confirmationKey,
        string operation,
        string tag,
        CancellationToken cancellationToken)
    {
        if (!TryParseBundle(sessionPayload, out var bundle, out var parseError))
        {
            return Failure(parseError, SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (string.IsNullOrWhiteSpace(identitySecret) || string.IsNullOrWhiteSpace(deviceId))
        {
            return Failure(
                "Identity secret and device id are required for mobile confirmations.",
                SteamReasonCodes.GuardNotConfigured);
        }

        try
        {
            var time = await GetAlignedSteamUnixTimeAsync(cancellationToken).ConfigureAwait(false);
            var query = BuildConfirmationQueryString(bundle.SteamId64, identitySecret, deviceId, time, tag);
            var url = $"https://steamcommunity.com/mobileconf/ajaxop?op={Uri.EscapeDataString(operation)}&{query}&cid={confirmationId}&ck={confirmationKey}";
            var response = await SendMobileCommunityRequestAsync(
                    HttpMethod.Get,
                    url,
                    bundle,
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);

            return ParseConfirmationOperationResponse(response, SteamReasonCodes.EndpointRejected);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to execute Steam confirmation operation {Operation}", operation);
            return Failure("Failed to execute mobile confirmation operation.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    private SteamOperationResult ParseConfirmationOperationResponse(MobileResponse response, string fallbackReason)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.RequestUri?.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Failure("Steam mobile confirmation session is unauthorized.", SteamReasonCodes.AuthSessionMissing, retryable: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Failure(
                $"Steam mobile confirmation operation failed: HTTP {(int)response.StatusCode}.",
                fallbackReason,
                retryable: (int)response.StatusCode >= 500);
        }

        try
        {
            using var json = JsonDocument.Parse(response.Body);
            var root = json.RootElement;
            if (TryReadSuccessFlag(root))
            {
                return new SteamOperationResult
                {
                    Success = true,
                    ReasonCode = SteamReasonCodes.None
                };
            }

            return Failure(
                GetJsonString(root, "message") ?? "Steam rejected mobile confirmation operation.",
                fallbackReason);
        }
        catch (JsonException)
        {
            return Failure("Steam mobile confirmation returned malformed JSON.", SteamReasonCodes.Unknown, retryable: true);
        }
    }

    private static SteamGuardLinkState FailLink(string message, string reasonCode, bool retryable = false)
    {
        return new SteamGuardLinkState
        {
            Success = false,
            Step = SteamGuardLinkStep.Failed,
            ErrorMessage = message,
            ReasonCode = reasonCode,
            Retryable = retryable
        };
    }

    private async Task<MobileResponse> SendMobileCommunityRequestAsync(
        HttpMethod method,
        string url,
        SteamSessionBundle bundle,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer()
        };

        ApplyMobileSessionCookies(handler.CookieContainer, bundle);
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
        };
        using var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        request.Headers.UserAgent.ParseAdd(_options.MobileUserAgent);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.Referrer = new Uri("https://steamcommunity.com/mobileconf/conf");
        if (request.Content is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new MobileResponse(
            response.StatusCode,
            response.IsSuccessStatusCode,
            body,
            response.RequestMessage?.RequestUri);
    }

    private static void ApplyMobileSessionCookies(CookieContainer cookieContainer, SteamSessionBundle bundle)
    {
        ApplySessionCookies(cookieContainer, bundle);
        var extraCookies = new[]
        {
            "mobileClient=android; path=/",
            "mobileClientVersion=777777 3.6.1; path=/"
        };

        foreach (var domain in new[] { "https://steamcommunity.com", "https://store.steampowered.com", "https://help.steampowered.com" })
        {
            var uri = new Uri(domain);
            foreach (var cookie in extraCookies)
            {
                cookieContainer.SetCookies(uri, cookie);
            }
        }
    }

    private async Task<ApiResponse> PostSteamApiFormAsync(
        string url,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebTimeoutSeconds))
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.UserAgent.ParseAdd(_options.MobileUserAgent);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new ApiResponse(response.IsSuccessStatusCode, response.StatusCode, body);
    }

    private async Task<long> GetAlignedSteamUnixTimeAsync(CancellationToken cancellationToken, bool forceSync = false)
    {
        var now = DateTimeOffset.UtcNow;
        var shouldSync = forceSync;
        lock (GuardTimeSyncLock)
        {
            shouldSync |= _guardTimeSyncAtUtc == DateTimeOffset.MinValue ||
                          (now - _guardTimeSyncAtUtc) > TimeSpan.FromMinutes(5);
        }

        if (!shouldSync)
        {
            lock (GuardTimeSyncLock)
            {
                return now.ToUnixTimeSeconds() + _guardTimeOffsetSeconds;
            }
        }

        try
        {
            var response = await PostSteamApiFormAsync(
                    "https://api.steampowered.com/ITwoFactorService/QueryTime/v1/",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["steamid"] = "0"
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.Success)
            {
                using var json = JsonDocument.Parse(response.Body);
                if (json.RootElement.TryGetProperty("response", out var root) &&
                    root.TryGetProperty("server_time", out var timeNode) &&
                    timeNode.TryGetInt64(out var serverTime))
                {
                    lock (GuardTimeSyncLock)
                    {
                        _guardTimeOffsetSeconds = serverTime - now.ToUnixTimeSeconds();
                        _guardTimeSyncAtUtc = now;
                    }

                    return serverTime;
                }
            }
        }
        catch
        {
            // ignored: fall back to local time + cached offset
        }

        lock (GuardTimeSyncLock)
        {
            _guardTimeSyncAtUtc = now;
            return now.ToUnixTimeSeconds() + _guardTimeOffsetSeconds;
        }
    }

    private static string BuildConfirmationQueryString(
        string steamId64,
        string identitySecret,
        string deviceId,
        long time,
        string tag)
    {
        var hash = GenerateConfirmationHashForTime(identitySecret, time, tag);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["p"] = deviceId,
            ["a"] = steamId64,
            ["k"] = hash,
            ["t"] = time.ToString(),
            ["m"] = "react",
            ["tag"] = tag
        };

        return string.Join("&", values.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string GenerateConfirmationHashForTime(string identitySecret, long time, string tag)
    {
        var secretBytes = Convert.FromBase64String(identitySecret);
        var tagBytes = string.IsNullOrEmpty(tag)
            ? []
            : Encoding.UTF8.GetBytes(tag.Length > 32 ? tag[..32] : tag);

        var payload = new byte[8 + tagBytes.Length];
        for (var i = 7; i >= 0; i--)
        {
            payload[i] = (byte)time;
            time >>= 8;
        }

        if (tagBytes.Length > 0)
        {
            Buffer.BlockCopy(tagBytes, 0, payload, 8, tagBytes.Length);
        }

        using var hmac = new HMACSHA1(secretBytes);
        var hash = hmac.ComputeHash(payload);
        return Convert.ToBase64String(hash);
    }

    private static SteamGuardConfirmationType ParseConfirmationType(JsonElement node)
    {
        if (!node.TryGetProperty("type", out var value))
        {
            return SteamGuardConfirmationType.Unknown;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var asInt))
        {
            return Enum.IsDefined(typeof(SteamGuardConfirmationType), asInt)
                ? (SteamGuardConfirmationType)asInt
                : SteamGuardConfirmationType.Unknown;
        }

        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<SteamGuardConfirmationType>(value.GetString(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return SteamGuardConfirmationType.Unknown;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && ReadBoolean(prop);

    private static bool ReadBoolean(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var asInt))
        {
            return asInt != 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static ulong ReadUInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return 0;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt64(out var asNumber))
        {
            return asNumber;
        }

        if (prop.ValueKind == JsonValueKind.String && ulong.TryParse(prop.GetString(), out var asString))
        {
            return asString;
        }

        return 0;
    }

    private static int ReadInt(JsonElement element, string propertyName, int fallback)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return fallback;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var asNumber))
        {
            return asNumber;
        }

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var asString))
        {
            return asString;
        }

        return fallback;
    }

    private static string GenerateAndroidDeviceId() => $"android:{Guid.NewGuid()}";

    private readonly record struct MobileResponse(
        HttpStatusCode StatusCode,
        bool IsSuccessStatusCode,
        string Body,
        Uri? RequestUri);

    private readonly record struct ApiResponse(
        bool Success,
        HttpStatusCode StatusCode,
        string Body);
}
