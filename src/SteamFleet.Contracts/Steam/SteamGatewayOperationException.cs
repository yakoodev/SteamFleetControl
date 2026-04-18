namespace SteamFleet.Contracts.Steam;

public sealed class SteamGatewayOperationException : Exception
{
    public SteamGatewayOperationException(
        string message,
        string reasonCode = SteamReasonCodes.Unknown,
        bool retryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? SteamReasonCodes.Unknown : reasonCode;
        Retryable = retryable;
    }

    public string ReasonCode { get; }
    public bool Retryable { get; }
}
