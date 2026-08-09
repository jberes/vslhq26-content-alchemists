using System.Net;
using Castmill.Core.Auth;
using Castmill.UI.Auth;
using Castmill.UI.Http;

namespace Castmill.UI.Tests;

/// <summary>
/// "Your session has expired" on image generation and the SEO keyword plan.
///
/// The trigger was measured, not guessed: generating two image variants takes ~123 seconds
/// server-side, while the app's HttpClient carried .NET's DEFAULT 100-second timeout. The
/// abort lands wherever the request happens to be — and if that is inside the silent refresh,
/// the server has already rotated (consumed) the single-use refresh token while the client
/// never stored its replacement. The next attempt then looks like token REUSE, which revokes
/// the whole family and signs the user out of a perfectly good session.
///
/// These pin the three behaviours that make that chain impossible.
/// </summary>
public sealed class TokenRefreshResilienceTests
{
    [Fact]
    public async Task A_transient_server_error_during_refresh_does_not_sign_the_user_out()
    {
        var provider = new FakeProvider("stored-refresh",
            _ => Task.FromException<AuthResponse>(new ApiException("upstream exploded", 503, null)));

        Assert.False(await provider.TryRefreshAsync());

        // The token says nothing about validity here, so it must survive. Clearing it turned
        // a blip during a slow generation into a forced sign-in.
        Assert.Equal("stored-refresh", provider.Stored);
    }

    [Fact]
    public async Task A_cancelled_refresh_does_not_sign_the_user_out()
    {
        var provider = new FakeProvider("stored-refresh",
            _ => Task.FromException<AuthResponse>(new TaskCanceledException("deadline fired")));

        Assert.False(await provider.TryRefreshAsync());
        Assert.Equal("stored-refresh", provider.Stored);
    }

    [Fact]
    public async Task Only_a_401_from_the_refresh_endpoint_clears_the_session()
    {
        var provider = new FakeProvider("stored-refresh",
            _ => Task.FromException<AuthResponse>(new UnauthorizedApiException(null)));

        Assert.False(await provider.TryRefreshAsync());

        // The server looked at this exact token and rejected it — the one definitive answer.
        Assert.Null(provider.Stored);
    }

    /// <summary>
    /// The old guard returned "already refreshed" whenever the LOCAL clock said the access
    /// token was still valid. But a caller only reaches here because the SERVER replied 401,
    /// so when the two disagree the server wins. The old check made the replay reuse the very
    /// token that had just been rejected, and the second 401 became "session expired".
    /// </summary>
    [Fact]
    public async Task A_401_forces_a_real_refresh_even_when_the_local_clock_says_the_token_is_fine()
    {
        var exchanges = 0;
        var provider = new FakeProvider("stored-refresh", _ =>
        {
            exchanges++;
            return Task.FromResult(new AuthResponse(
                "fresh-access", DateTimeOffset.UtcNow.AddMinutes(15),
                "next-refresh", DateTimeOffset.UtcNow.AddDays(30)));
        });

        // A token the client believes is good for another 15 minutes.
        await provider.StoreAsync("stale-access", DateTimeOffset.UtcNow.AddMinutes(15), "stored-refresh");

        Assert.True(await provider.TryRefreshAsync());

        Assert.Equal(1, exchanges);
        Assert.Equal("fresh-access", provider.AccessToken);
        Assert.Equal("next-refresh", provider.Stored);
    }

    [Fact]
    public async Task Parallel_401s_exchange_the_single_use_token_exactly_once()
    {
        var exchanges = 0;
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider("stored-refresh", async _ =>
        {
            Interlocked.Increment(ref exchanges);
            await gate.Task;
            return new AuthResponse(
                "fresh-access", DateTimeOffset.UtcNow.AddMinutes(15),
                "next-refresh", DateTimeOffset.UtcNow.AddDays(30));
        });

        await provider.StoreAsync("stale-access", DateTimeOffset.UtcNow.AddMinutes(15), "stored-refresh");

        var all = Enumerable.Range(0, 8).Select(_ => provider.TryRefreshAsync()).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(all);

        // Eight concurrent 401s, ONE exchange. A second would be read as reuse and revoke
        // the family — the exact failure this whole design exists to avoid.
        Assert.Equal(1, exchanges);
        Assert.All(results, Assert.True);
    }

    /// <summary>
    /// The Mac Catalyst Keychain outage, verbatim: the app shipped without the
    /// keychain-access-groups entitlement, so every SecureStorage write threw (swallowed, per
    /// the desktop provider's contract) and every read came back empty. Refresh used to read
    /// the token back FROM STORAGE, so exactly 15 minutes after sign-in the access token
    /// expired with nothing to renew it and the whole app collapsed into "session expired".
    /// Memory is authoritative now — a live session must never depend on persistence working.
    /// </summary>
    [Fact]
    public async Task A_session_survives_storage_that_silently_loses_every_write()
    {
        var provider = new BrokenStorageProvider(_ => Task.FromResult(new AuthResponse(
            "fresh-access", DateTimeOffset.UtcNow.AddMinutes(15),
            "next-refresh", DateTimeOffset.UtcNow.AddDays(30))));

        // Sign-in: storage loses the refresh token, exactly as the entitlement-less build did.
        await provider.StoreAsync("access", DateTimeOffset.UtcNow.AddMinutes(15), "login-refresh");

        // Minute 15: the access token has expired, a request 401s, refresh runs.
        Assert.True(await provider.TryRefreshAsync());
        Assert.Equal("fresh-access", provider.AccessToken);
        Assert.Equal("login-refresh", provider.Exchanged);
    }

    /// <summary>Storage that behaves like SecureStorage without the Keychain entitlement:
    /// writes vanish (the desktop provider swallows the throw), reads find nothing.</summary>
    private sealed class BrokenStorageProvider(Func<string, Task<AuthResponse>> exchange) : TokenProviderBase(
        () => throw new InvalidOperationException("AuthClient must not be resolved."))
    {
        public string? Exchanged { get; private set; }

        protected override Task<string?> ReadRefreshTokenAsync() => Task.FromResult<string?>(null);

        protected override Task WriteRefreshTokenAsync(string refreshToken) => Task.CompletedTask;

        protected override Task DeleteRefreshTokenAsync() => Task.CompletedTask;

        protected override Task<AuthResponse> RefreshAsync(string refreshToken)
        {
            Exchanged = refreshToken;
            return exchange(refreshToken);
        }
    }

    private sealed class FakeProvider : TokenProviderBase
    {
        private readonly Func<string, Task<AuthResponse>> _exchange;

        public FakeProvider(string? stored, Func<string, Task<AuthResponse>> exchange)
            : base(() => throw new InvalidOperationException("AuthClient must not be resolved."))
        {
            Stored = stored;
            _exchange = exchange;
        }

        public string? Stored { get; private set; }

        protected override Task<string?> ReadRefreshTokenAsync() => Task.FromResult(Stored);

        protected override Task WriteRefreshTokenAsync(string refreshToken)
        {
            Stored = refreshToken;
            return Task.CompletedTask;
        }

        protected override Task DeleteRefreshTokenAsync()
        {
            Stored = null;
            return Task.CompletedTask;
        }

        // Stands in for AuthClient.RefreshAsync, which is not virtual.
        protected override Task<AuthResponse> RefreshAsync(string refreshToken) => _exchange(refreshToken);
    }
}
