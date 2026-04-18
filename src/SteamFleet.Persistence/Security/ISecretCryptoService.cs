namespace SteamFleet.Persistence.Security;

public interface ISecretCryptoService
{
    string Version { get; }
    string Encrypt(string plainText);
    string? Decrypt(string? cipherText);
}
