using System.Security.Cryptography;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Configuration;

namespace Castmill.Api.Tests;

public sealed class SecretCipherTests
{
    private static SecretCipher CreateCipher(string? key = null)
    {
        key ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Castmill:EncryptionKey", key)])
            .Build();
        return new SecretCipher(config);
    }

    [Fact]
    public void Roundtrip_restores_plaintext()
    {
        var cipher = CreateCipher();
        const string secret = "https://myproject.openai.azure.com|sk-abc123";
        Assert.Equal(secret, cipher.Decrypt(cipher.Encrypt(secret)));
    }

    [Fact]
    public void Same_plaintext_encrypts_to_different_ciphertexts()
    {
        var cipher = CreateCipher();
        Assert.NotEqual(cipher.Encrypt("value"), cipher.Encrypt("value"));
    }

    [Fact]
    public void Tampered_ciphertext_fails_authentication()
    {
        var cipher = CreateCipher();
        var payload = Convert.FromBase64String(cipher.Encrypt("value"));
        payload[^1] ^= 0xFF; // flip one bit anywhere → GCM tag check must fail
        var tampered = Convert.ToBase64String(payload);
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(tampered));
    }

    [Fact]
    public void Ciphertext_from_a_different_key_is_rejected()
    {
        var encrypted = CreateCipher().Encrypt("value");
        Assert.ThrowsAny<CryptographicException>(() => CreateCipher().Decrypt(encrypted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!!")]
    [InlineData("c2hvcnQ=")] // "short" — valid base64, wrong length
    public void Invalid_key_configuration_refuses_startup(string? key)
    {
        var values = new List<KeyValuePair<string, string?>>();
        if (key is not null)
        {
            values.Add(new("Castmill:EncryptionKey", key));
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<InvalidOperationException>(() => new SecretCipher(config));
    }
}
