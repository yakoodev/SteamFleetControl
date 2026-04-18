namespace SteamFleet.Contracts.Auth;

public sealed class LoginResponse
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
}
