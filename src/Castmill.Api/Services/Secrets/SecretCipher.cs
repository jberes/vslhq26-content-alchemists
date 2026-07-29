using System.Security.Cryptography;

namespace Castmill.Api.Services.Secrets;

public interface ISecretCipher
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}

/// <summary>
/// AES-256-GCM for at-rest user secrets (Foundry credentials, broker tokens).
/// Wire format (base64): 12-byte nonce ‖ 16-byte tag ‖ ciphertext.
/// GCM is authenticated: any tampering with the stored value fails decryption
/// loudly instead of yielding garbage. A fresh random nonce per encryption
/// means identical plaintexts never produce identical rows.
/// </summary>
public sealed class SecretCipher : ISecretCipher
{
    public const string ConfigKey = "Castmill:EncryptionKey";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SecretCipher(IConfiguration configuration)
    {
        var encoded = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is missing. Generate one with: openssl rand -base64 32 " +
                "(dev: appsettings.Development.json; prod: App Service setting / Key Vault).");
        }
        try
        {
            _key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{ConfigKey} must be base64.");
        }
        if (_key.Length != 32)
        {
            throw new InvalidOperationException($"{ConfigKey} must decode to exactly 32 bytes (AES-256).");
        }
    }

    public string Encrypt(string plaintext)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipherBytes = new byte[plainBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var payload = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipherBytes.CopyTo(payload, NonceSize + TagSize);
        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string ciphertext)
    {
        var payload = Convert.FromBase64String(ciphertext);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext payload is truncated.");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipherBytes = payload.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }
}
