using System.Globalization;
using Microsoft.Extensions.Logging;
using SteamFleet.Contracts.Steam;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
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
}
