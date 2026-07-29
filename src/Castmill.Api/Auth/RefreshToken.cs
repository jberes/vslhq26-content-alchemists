namespace Castmill.Api.Auth;

/// <summary>
/// Persisted refresh token. Only the SHA-256 hash of the token is stored —
/// a database leak cannot be replayed as a credential. Tokens rotate on every
/// use; presenting an already-rotated token revokes the whole family
/// (reuse detection — the standard defense against stolen-token replay).
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>All rotations of one sign-in share a family; reuse revokes the family.</summary>
    public Guid FamilyId { get; set; }
    /// <summary>SHA-256 of the token value, Base64. The plaintext is never persisted.</summary>
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && UsedAt is null && now < ExpiresAt;
}
