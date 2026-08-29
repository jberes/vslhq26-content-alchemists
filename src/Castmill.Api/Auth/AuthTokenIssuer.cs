using Castmill.Api.Data;
using Castmill.Core.Auth;

namespace Castmill.Api.Auth;

public interface IAuthTokenIssuer
{
    Task<AuthResponse> IssueAsync(
        CastmillUser user,
        Guid familyId,
        DateTimeOffset now,
        CancellationToken ct = default);
}

public sealed class AuthTokenIssuer(CastmillDbContext db, ITokenService tokens) : IAuthTokenIssuer
{
    public async Task<AuthResponse> IssueAsync(
        CastmillUser user,
        Guid familyId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var (access, accessExpires) = tokens.CreateAccessToken(user);
        var (plainRefresh, entity) = tokens.CreateRefreshToken(user.Id, familyId, now);
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return new AuthResponse(access, accessExpires, plainRefresh, entity.ExpiresAt);
    }
}