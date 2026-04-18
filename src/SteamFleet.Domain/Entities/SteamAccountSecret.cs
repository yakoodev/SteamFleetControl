namespace SteamFleet.Domain.Entities;

public sealed class SteamAccountSecret : EntityBase
{
    public Guid AccountId { get; set; }
    public SteamAccount? Account { get; set; }
    public string? EncryptedPassword { get; set; }
    public string? EncryptedSharedSecret { get; set; }
    public string? EncryptedIdentitySecret { get; set; }
    public string? EncryptedSessionPayload { get; set; }
    public string? EncryptedRecoveryPayload { get; set; }
    public string EncryptionVersion { get; set; } = "aes-gcm-v1";
}
