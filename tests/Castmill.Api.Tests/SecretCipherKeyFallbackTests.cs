using System.Security.Cryptography;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Configuration;

namespace Castmill.Api.Tests;

/// <summary>
/// ADR-079. One database is read by several hosts (the local API and the App Service, whose
/// key the deploy tool generates independently), so a secret row is encrypted under whichever
/// host wrote it. Before the fallback list existed, every secret entered through one host read
/// as lost on the other — six RE-ENTER cards with no stated cause, and re-entering was the only
/// offered action, which throws away a value that was never damaged.
/// </summary>
public sealed class SecretCipherKeyFallbackTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static SecretCipher Cipher(string active, params string[] previous)
    {
        var settings = new Dictionary<string, string?>
        {
            [SecretCipher.ConfigKey] = active,
        };
        for (var i = 0; i < previous.Length; i++)
        {
            settings[$"{SecretCipher.PreviousKeysConfigKey}:{i}"] = previous[i];
        }
        return new SecretCipher(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void A_secret_written_by_another_host_is_readable_when_that_key_is_a_previous_key()
    {
        var otherHostKey = NewKey();
        var stored = Cipher(otherHostKey).Encrypt("sk-live-value");

        var thisHost = Cipher(NewKey(), otherHostKey);

        Assert.Equal("sk-live-value", thisHost.Decrypt(stored));
    }

    /// <summary>The active key is what writes, always — a value written here must stay
    /// readable on a host that holds only the active key.</summary>
    [Fact]
    public void Writes_use_the_active_key_not_a_previous_one()
    {
        var activeKey = NewKey();
        var retired = NewKey();

        var written = Cipher(activeKey, retired).Encrypt("fresh");

        Assert.Equal("fresh", Cipher(activeKey).Decrypt(written));
    }

    /// <summary>
    /// A read must not re-encrypt under the active key: repairing this host that way would
    /// make the row unreadable on the host that wrote it, and the two would take turns
    /// invalidating each other's secrets.
    /// </summary>
    [Fact]
    public void A_fallback_read_leaves_the_stored_ciphertext_alone()
    {
        var otherHostKey = NewKey();
        var stored = Cipher(otherHostKey).Encrypt("shared-secret");
        var thisHost = Cipher(NewKey(), otherHostKey);

        thisHost.Decrypt(stored);

        // The other host still opens the very same stored value.
        Assert.Equal("shared-secret", Cipher(otherHostKey).Decrypt(stored));
    }

    [Fact]
    public void Several_previous_keys_are_each_tried()
    {
        var third = NewKey();
        var stored = Cipher(third).Encrypt("third-host");

        var reader = Cipher(NewKey(), NewKey(), third);

        Assert.Equal("third-host", reader.Decrypt(stored));
    }

    /// <summary>A genuinely unopenable value still fails — and the message names the active
    /// key's fingerprint, so the mismatch is diagnosable from one log line.</summary>
    [Fact]
    public void An_unopenable_value_reports_the_active_key_fingerprint()
    {
        var stored = Cipher(NewKey()).Encrypt("unreachable");
        var reader = Cipher(NewKey(), NewKey());

        var ex = Assert.Throws<CryptographicException>(() => reader.Decrypt(stored));

        Assert.Contains(reader.ActiveKeyFingerprint, ex.Message, StringComparison.Ordinal);
        Assert.Contains(SecretCipher.PreviousKeysConfigKey, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The fingerprint identifies a key without being reversible to it.</summary>
    [Fact]
    public void The_active_fingerprint_is_stable_per_key_and_never_the_key_itself()
    {
        var key = NewKey();

        var fingerprint = Cipher(key).ActiveKeyFingerprint;

        Assert.Equal(fingerprint, Cipher(key, NewKey()).ActiveKeyFingerprint);
        Assert.NotEqual(fingerprint, Cipher(NewKey()).ActiveKeyFingerprint);
        Assert.Equal(12, fingerprint.Length);
        Assert.DoesNotContain(fingerprint, key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A malformed fallback fails at startup, not at the moment a producer's secret
    /// silently reads as absent.</summary>
    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")]
    public void A_malformed_previous_key_stops_the_process(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Cipher(NewKey(), bad));

        Assert.Contains(SecretCipher.PreviousKeysConfigKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_previous_keys_configured_behaves_exactly_as_before()
    {
        var key = NewKey();
        var only = Cipher(key);

        Assert.Equal("plain", only.Decrypt(only.Encrypt("plain")));
        Assert.Throws<CryptographicException>(() => only.Decrypt(Cipher(NewKey()).Encrypt("x")));
    }
}
