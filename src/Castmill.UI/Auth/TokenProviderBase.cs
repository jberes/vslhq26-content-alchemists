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
        await _refreshLock.WaitAsync();
        try
        {
            // Another caller may have refreshed while this one waited.
            if (_accessToken is not null && _accessExpiresAt > DateTimeOffset.UtcNow.AddSeconds(5))
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
            var response = await authClient().RefreshAsync(refreshToken);
            await StoreAsync(response.AccessToken, response.AccessTokenExpiresAt, response.RefreshToken);
            return true;
        }
        catch (Http.ApiException)
        {
            // Revoked, expired, or reused: the stored token is worthless, so drop it
            // rather than retrying with it on every subsequent request.
            await ClearAsync();
            return false;
        }
        catch (HttpRequestException)
        {
            // Offline. Keep the stored token — it may still be valid once there is a
            // network again — but report that we are not signed in.
            return false;
        }
    }
}
