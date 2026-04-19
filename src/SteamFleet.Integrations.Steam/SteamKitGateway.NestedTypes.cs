using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Security.Cryptography;
using SteamFleet.Contracts.Steam;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
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
