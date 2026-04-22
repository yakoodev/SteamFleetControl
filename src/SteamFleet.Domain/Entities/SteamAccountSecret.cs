namespace SteamFleet.Domain.Entities;

public sealed class SteamAccountSecret : EntityBase
{
    public Guid AccountId { get; set; }
    public SteamAccount? Account { get; set; }
    public string? EncryptedPassword { get; set; }
    public string? EncryptedSharedSecret { get; set; }
    public string? EncryptedIdentitySecret { get; set; }
    public string? EncryptedDeviceId { get; set; }
    public string? EncryptedRevocationCode { get; set; }
    public string? EncryptedSerialNumber { get; set; }
    public string? EncryptedTokenGid { get; set; }
    public string? EncryptedUri { get; set; }
    public string? EncryptedLinkStatePayload { get; set; }
    public bool? GuardFullyEnrolled { get; set; }
    public string? EncryptedSessionPayload { get; set; }
    public string? EncryptedRecoveryPayload { get; set; }
    public string EncryptionVersion { get; set; } = "aes-gcm-v1";
}
