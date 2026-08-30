using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Auth;

public sealed record RegisterRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, MinLength(12), MaxLength(128)] string Password,
    [property: Required, MaxLength(100)] string DisplayName);

public sealed record LoginRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, MaxLength(128)] string Password);

public sealed record RefreshRequest(
    [property: Required, MaxLength(512)] string RefreshToken);

public sealed record ChangePasswordRequest(
    [property: Required, MaxLength(128)] string CurrentPassword,
    [property: Required, MinLength(12), MaxLength(128)] string NewPassword);

/// <summary>
/// Auth response: short-lived access JWT + a rotating refresh token.
/// The refresh token value appears exactly once, here; only its SHA-256 hash is persisted.
/// </summary>
public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record MeResponse(
    Guid UserId,
    Guid TenantId,
    string Email,
    string DisplayName,
    bool HasAvatar = false);
