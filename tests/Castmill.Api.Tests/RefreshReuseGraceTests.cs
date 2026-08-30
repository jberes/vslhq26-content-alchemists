using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Castmill.Api.Tests;

/// <summary>
/// Refresh tokens are single-use, and strict single-use turned three innocent events into
/// "your session has expired": the app dying between the exchange and storing its new token,
/// two windows racing the same stored token, and a network retry replaying an answered
/// request. All three present a JUST-consumed token — which strict rotation reads as theft
/// and answers by revoking the whole family.
///
/// The grace window (Auth0's "reuse interval") makes a replay within seconds rotate again
/// instead. Outside the window, reuse detection is exactly as brutal as before — the last
/// test here proves the teeth are still in.
/// </summary>
[Collection("api")]
public sealed class RefreshReuseGraceTests(CastmillApiFactory factory)
{
    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"grace-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Grace Tester"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    [Fact]
    public async Task A_replay_within_the_grace_rotates_again_instead_of_killing_the_session()
    {
        var client = factory.CreateClient();
        var login = await RegisterAsync(client);

        // First use consumes the token; the immediate replay is the crash/race/retry shape.
        var first = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // Both parties hold working tokens — the point of the grace is that neither the
        // crashed client nor the surviving one is signed out.
        var fromFirst = (await first.Content.ReadFromJsonAsync<AuthResponse>())!;
        var fromReplay = (await replay.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(fromFirst.RefreshToken, fromReplay.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, fromFirst.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, fromReplay.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Outside_the_grace_reuse_still_revokes_the_whole_family()
    {
        // Grace zero restores strict single-use — the pre-grace behaviour, verbatim.
        await using var app = factory.WithWebHostBuilder(b =>
            b.UseSetting("Jwt:RefreshReuseGraceSeconds", "0"));
        var client = app.CreateClient();
        var login = await RegisterAsync(client);

        var first = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var successor = (await first.Content.ReadFromJsonAsync<AuthResponse>())!;

        // The replay is refused…
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await RefreshAsync(client, login.RefreshToken)).StatusCode);

        // …and the SUCCESSOR dies with it: family revocation is what makes reuse detection a
        // real defence rather than a speed bump, and the grace must not have dulled it.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await RefreshAsync(client, successor.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_gets_no_grace()
    {
        var client = factory.CreateClient();
        var login = await RegisterAsync(client);

        // Logout revokes the family; the grace is for rotation races, never for revocation.
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        (await client.PostAsJsonAsync("/api/v1/auth/logout", new { })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await RefreshAsync(client, login.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_refresh_and_logout_leave_no_active_tokens()
    {
        var client = factory.CreateClient();
        var login = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/me");
        Assert.NotNull(me);

        await using var lockScope = factory.Services.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        await using var lockTransaction = await lockDb.Database.BeginTransactionAsync();
        await AuthEndpoints.AcquireRefreshTokenLockAsync(
            lockDb,
            me.UserId,
            TestContext.Current.CancellationToken);

        var refreshTask = RefreshAsync(client, login.RefreshToken);
        var logoutTask = client.PostAsJsonAsync("/api/v1/auth/logout", new { });
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await lockTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(refreshTask, logoutTask);
        Assert.True(responses[0].StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.NoContent, responses[1].StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        Assert.Equal(0, await verifyDb.RefreshTokens.CountAsync(
            token => token.UserId == me.UserId
                && token.RevokedAt == null
                && token.UsedAt == null
                && token.ExpiresAt > DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
    }
}
