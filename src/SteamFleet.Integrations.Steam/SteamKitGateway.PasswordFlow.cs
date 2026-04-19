using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamFleet.Contracts.Steam;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
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
}

