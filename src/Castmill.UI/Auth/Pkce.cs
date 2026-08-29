using System.Security.Cryptography;
using System.Text;

namespace Castmill.UI.Auth;

public sealed record PkcePair(string CodeVerifier, string CodeChallenge);

public static class Pkce
{
    public static PkcePair Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new PkcePair(verifier, CreateChallenge(verifier));
    }

    public static string CreateChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}