namespace Castmill.UI.Platform;

/// <summary>
/// Token custody, implemented per shell (Roadmap §2.2). The access JWT is held in memory
/// only; the rotating refresh token goes to OS-protected storage on desktop
/// (<c>SecureStorage</c>) and browser storage on web.
///
/// Nothing outside this interface and the HTTP chokepoint ever sees a token.
/// </summary>
public interface IAuthTokenProvider
{
    /// <summary>The current access token, or null when signed out or expired.</summary>
    string? AccessToken { get; }

    bool IsSignedIn { get; }

    /// <summary>Raised on sign-in, sign-out and silent restore so chrome can re-render.</summary>
    event Action? Changed;

    /// <summary>
    /// Reads the persisted refresh token on cold start and exchanges it for a fresh access
    /// token, so a returning user is already signed in when the first screen paints.
    /// Returns false when there is nothing stored or the token has been revoked.
    /// </summary>
    Task<bool> TryRestoreAsync();

    /// <summary>Stores a freshly issued token pair. The refresh token is persisted; the access token is not.</summary>
    Task StoreAsync(string accessToken, DateTimeOffset accessExpiresAt, string refreshToken);

    /// <summary>
    /// Exchanges the stored refresh token for a new pair. Called by the chokepoint on a
    /// 401 — never by feature code.
    /// </summary>
    Task<bool> TryRefreshAsync();

    /// <summary>Clears both tokens locally. Revoking server-side is the caller's job.</summary>
    Task ClearAsync();
}
