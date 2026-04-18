using System.Security.Cryptography;
using System.Text;

namespace SteamFleet.Persistence.Security;

public sealed class AesGcmSecretCryptoService : ISecretCryptoService
{
    private readonly byte[] _key;

    public AesGcmSecretCryptoService(string masterKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(masterKeyBase64))
        {
            throw new InvalidOperationException("SECRETS_MASTER_KEY_B64 is required.");
        }

        _key = Convert.FromBase64String(masterKeyBase64.Trim());
        if (_key.Length is not (16 or 24 or 32))
        {
            throw new InvalidOperationException("SECRETS_MASTER_KEY_B64 must decode to 16/24/32 bytes.");
        }
    }

    public string Version => "aes-gcm-v1";

    public string Encrypt(string plainText)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintextBytes.Length];

        using var aesGcm = new AesGcm(_key, 16);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);

        return Convert.ToBase64String(payload);
    }

    public string? Decrypt(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return null;
        }

        var payload = Convert.FromBase64String(cipherText);
        if (payload.Length < 28)
        {
            throw new CryptographicException("Invalid encrypted payload.");
        }

        var nonce = payload[..12];
        var tag = payload[12..28];
        var ciphertext = payload[28..];
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_key, 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
