using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Castmill.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "castmill-api";
    public string Audience { get; set; } = "castmill-clients";
    /// <summary>Supplied via user-secrets (dev) or environment/Key Vault (prod). Never in appsettings.json.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// How long a JUST-ROTATED refresh token may be presented again without being read as
    /// theft. Without any grace, three innocent events revoked the whole session family and
    /// produced "your session has expired": a crash between the exchange and the client
    /// storing its new token, two windows racing the same stored token, and a network retry
    /// replaying a request the server had already answered. Auth0 ships the same leeway.
    /// Zero disables the grace and restores strict single-use.
    /// </summary>
    public int RefreshReuseGraceSeconds { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;
}

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(CastmillUser user);
    (string PlainToken, RefreshToken Entity) CreateRefreshToken(Guid userId, Guid familyId, DateTimeOffset now);
    string HashRefreshToken(string plainToken);
}

public sealed class TokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(CastmillUser user)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new("tenant", user.TenantId.ToString()),
            new("name", user.DisplayName),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string PlainToken, RefreshToken Entity) CreateRefreshToken(Guid userId, Guid familyId, DateTimeOffset now)
    {
        // 256 bits of CSPRNG entropy; the plaintext exists only in the response body.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plain = Base64UrlEncoder.Encode(bytes);

        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = HashRefreshToken(plain),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.RefreshTokenDays),
        };
        return (plain, entity);
    }

    public string HashRefreshToken(string plainToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plainToken)));
}
