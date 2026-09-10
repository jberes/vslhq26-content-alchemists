using System.Security.Cryptography;

namespace Castmill.Api.Services.Secrets;

public interface ISecretCipher
{
    string Encrypt(string plaintext);
    /// <summary>Opens a stored value with the active key, or any retired key still configured.</summary>
    string Decrypt(string ciphertext);
    /// <summary>
    /// Short fingerprint of the ACTIVE key. Logged at startup and shown on the re-enter
    /// prompt: with one database read by several hosts, "which key is this host on" is the
    /// first question a lost secret raises, and it must be answerable without the key itself.
    /// </summary>
    string ActiveKeyFingerprint => string.Empty;
}

/// <summary>
/// AES-256-GCM for at-rest user secrets (Foundry credentials, broker tokens).
/// Wire format (base64): 12-byte nonce ‖ 16-byte tag ‖ ciphertext.
/// GCM is authenticated: any tampering with the stored value fails decryption
/// loudly instead of yielding garbage. A fresh random nonce per encryption
/// means identical plaintexts never produce identical rows.
///
/// <para><b>Why more than one key (ADR-079).</b> Writes always use
/// <c>Castmill:EncryptionKey</c>; reads fall back through
/// <c>Castmill:PreviousEncryptionKeys</c>. Several hosts share one database — the local API
/// and the App Service, whose key the deploy tool generates independently — so a row is
/// encrypted under whichever host wrote it. Without a fallback list, every secret entered
/// through one host reads as lost on the other, and the page can only offer "re-enter",
/// which silently discards a perfectly good value. Reads deliberately do NOT re-encrypt
/// under the active key: that would repair this host at the cost of breaking the other one,
/// and the two would then take turns invalidating each other's rows.</para>
/// </summary>
public sealed class SecretCipher : ISecretCipher
{
    public const string ConfigKey = "Castmill:EncryptionKey";
    /// <summary>Retired keys still needed to READ existing rows. Never used to write.</summary>
    public const string PreviousKeysConfigKey = "Castmill:PreviousEncryptionKeys";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;
    /// <summary>Read-only fallbacks, in configured order. Empty is the normal single-key case.</summary>
    private readonly byte[][] _previousKeys;

    public string ActiveKeyFingerprint { get; }

    public SecretCipher(IConfiguration configuration)
    {
        var encoded = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is missing. Generate one with: openssl rand -base64 32 " +
                "(dev: appsettings.Development.json; prod: App Service setting / Key Vault).");
        }
        _key = ParseKey(encoded, ConfigKey);
        ActiveKeyFingerprint = Fingerprint(_key);
        _previousKeys = [.. ReadPreviousKeys(configuration)];
    }

    /// <summary>
    /// Accepts a JSON array (<c>"PreviousEncryptionKeys": ["…", "…"]</c>) or the indexed
    /// double-underscore form App Service settings use. A malformed entry throws here rather
    /// than degrading into "that secret is gone" at read time.
    /// </summary>
    private static IEnumerable<byte[]> ReadPreviousKeys(IConfiguration configuration)
    {
        var section = configuration.GetSection(PreviousKeysConfigKey);
        var index = 0;
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                yield return ParseKey(child.Value, $"{PreviousKeysConfigKey}[{index}]");
            }
            index++;
        }

        // A single un-indexed value is the shape a hand-edited setting most often takes.
        if (index == 0 && !string.IsNullOrWhiteSpace(section.Value))
        {
            yield return ParseKey(section.Value, PreviousKeysConfigKey);
        }
    }

    private static byte[] ParseKey(string encoded, string configKey)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{configKey} must be base64.");
        }
        return key.Length == 32
            ? key
            : throw new InvalidOperationException(
                $"{configKey} must decode to exactly 32 bytes (AES-256).");
    }

    /// <summary>
    /// First 12 hex of SHA-256 over the key. Safe to log and to show a producer: it says
    /// which key a host holds without being reversible to the key.
    /// </summary>
    private static string Fingerprint(byte[] key) =>
        Convert.ToHexString(SHA256.HashData(key))[..12].ToLowerInvariant();

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

        // The active key first: the common case pays nothing for the fallback list.
        if (TryDecrypt(payload, _key, out var plaintext))
        {
            return plaintext;
        }
        foreach (var previous in _previousKeys)
        {
            if (TryDecrypt(payload, previous, out var recovered))
            {
                return recovered;
            }
        }

        // Names the active key, so the mismatch is diagnosable from the log line alone.
        throw new CryptographicException(
            $"The stored value cannot be opened with the active encryption key ({ActiveKeyFingerprint}) "
            + $"or any of the {_previousKeys.Length} configured previous key(s). It was written under a "
            + $"different {ConfigKey} — add that key to {PreviousKeysConfigKey} to read it.");
    }

    private static bool TryDecrypt(byte[] payload, byte[] key, out string plaintext)
    {
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipherBytes = payload.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        catch (CryptographicException)
        {
            // Wrong key for this row — GCM's tag check is exactly how we learn that.
            plaintext = string.Empty;
            return false;
        }
        plaintext = System.Text.Encoding.UTF8.GetString(plainBytes);
        return true;
    }
}
