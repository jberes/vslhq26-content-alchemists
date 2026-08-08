using Castmill.Core.Auth;
using Castmill.UI.Platform;

namespace Castmill.UI.Auth;

/// <summary>
/// Everything about token custody that is identical in both shells: the in-memory access
/// token, expiry tracking, cold-start restore and single-flight refresh. The only thing a
/// shell supplies is <em>where the refresh token is written</em>, via the three abstract
/// members — <c>SecureStorage</c> on desktop, browser storage on web (Roadmap §2.2).
///
/// The access token is deliberately never persisted: it is short-lived, and writing it
/// anywhere would widen the blast radius of a compromised device for no benefit.
/// </summary>
public abstract class TokenProviderBase(Func<AuthClient> authClient) : IAuthTokenProvider, IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;

    public string? AccessToken => _accessToken;

    public bool IsSignedIn => _accessToken is not null;

    public event Action? Changed;

    protected abstract Task<string?> ReadRefreshTokenAsync();

    protected abstract Task WriteRefreshTokenAsync(string refreshToken);

    protected abstract Task DeleteRefreshTokenAsync();

    /// <summary>
    /// The single call to the refresh endpoint, as a seam. AuthClient is a concrete class with
    /// non-virtual methods, so without this the token-custody rules below — which failure
    /// modes clear the session and which keep it — cannot be tested at all.
    /// </summary>
    protected virtual Task<AuthResponse> RefreshAsync(string refreshToken) =>
        // CancellationToken.None deliberately: see ExchangeAsync.
        authClient().RefreshAsync(refreshToken, CancellationToken.None);

    public async Task<bool> TryRestoreAsync()
    {
        var stored = await ReadRefreshTokenAsync();
        return !string.IsNullOrEmpty(stored) && await ExchangeAsync(stored!);
    }

    public async Task StoreAsync(string accessToken, DateTimeOffset accessExpiresAt, string refreshToken)
    {
        _accessToken = accessToken;
        _accessExpiresAt = accessExpiresAt;
        await WriteRefreshTokenAsync(refreshToken);
        Changed?.Invoke();
    }

    public async Task<bool> TryRefreshAsync()
    {
        // Single-flight: several parallel calls can 401 at once, and refresh tokens are
        // single-use — a second exchange with the same token would look like token reuse
        // and get the whole family revoked server-side.
        // Captured BEFORE the wait: the whole point is to detect a refresh that happened
        // while this caller was queued on the lock.
        var tokenOnEntry = _accessToken;

        await _refreshLock.WaitAsync();
        try
        {
            // Another caller refreshed while this one waited — recognised by the access token
            // having actually CHANGED, not by the local clock. Trusting _accessExpiresAt here
            // was wrong: the caller only gets in because the SERVER returned 401, so when the
            // two disagree the server is right. The old check returned "refreshed" without
            // refreshing, the replay reused the same rejected token, and the user was told
            // their session had expired while holding a token the client thought was fine.
            if (_accessToken is not null && !ReferenceEquals(_accessToken, tokenOnEntry))
            {
                return true;
            }

            var stored = await ReadRefreshTokenAsync();
            return !string.IsNullOrEmpty(stored) && await ExchangeAsync(stored!);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task ClearAsync()
    {
        _accessToken = null;
        _accessExpiresAt = default;
        await DeleteRefreshTokenAsync();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshLock.Dispose();
        }
    }

    private async Task<bool> ExchangeAsync(string refreshToken)
    {
        try
        {
            // The seam above uses CancellationToken.None deliberately: this refresh is nested
            // inside whatever request triggered it, and that request's deadline is not this
            // one's. A refresh cancelled part-way is the worst possible outcome — the server
            // may already have rotated (consumed) the token while we never stored its
            // replacement, so the next attempt looks like REUSE and the server revokes the
            // entire family.
            var response = await RefreshAsync(refreshToken);
            await StoreAsync(response.AccessToken, response.AccessTokenExpiresAt, response.RefreshToken);
            return true;
        }
        catch (Http.UnauthorizedApiException)
        {
            // The ONLY definitive answer: the server looked at this token and rejected it.
            // Revoked, expired or reused — it is worthless, so drop it rather than retrying
            // with it on every subsequent request.
            await ClearAsync();
            return false;
        }
        catch (Http.ApiException)
        {
            // A 500, a 503, a proxy error. This says nothing about whether the token is
            // still good, and clearing here signed people out over a transient blip — which
            // is exactly what a long image generation could provoke. Keep it and retry later.
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Offline, or the ambient request deadline fired. Keep the stored token — it may
            // still be valid once there is a network again — but report that we are not
            // signed in right now.
            return false;
        }
    }
}
