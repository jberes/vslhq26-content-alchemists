using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class AuthFlowTests(CastmillApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private static RegisterRequest NewUser(string? email = null) => new(
        email ?? $"user-{Guid.NewGuid():N}@example.com",
        "correct-horse-battery-staple",
        "Test User");

    private async Task<(RegisterRequest User, AuthResponse Tokens)> RegisterAsync()
    {
        var user = NewUser();
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", user);
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(tokens);
        return (user, tokens);
    }

    [Fact]
    public async Task Health_is_anonymous_and_returns_200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Web_shell_serves_root_and_deep_links_without_masking_api_404s()
    {
        var root = await _client.GetAsync("/");
        var deepLink = await _client.GetAsync("/campaigns/new");
        var missingApi = await _client.GetAsync("/api/v1/not-a-real-route");

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Castmill</title>", await root.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
        Assert.Equal("text/html", deepLink.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
        Assert.NotEqual("text/html", missingApi.Content.Headers.ContentType?.MediaType);

        var bootstrap = await _client.GetAsync("/_framework/blazor.webassembly.js");
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.Equal("text/javascript", bootstrap.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Register_then_me_roundtrip_returns_identity()
    {
        var (user, tokens) = await RegisterAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.Equal(user.Email, me.Email);
        Assert.NotEqual(Guid.Empty, me.TenantId);
    }

    [Fact]
    public async Task Me_without_token_returns_401()
    {
        var response = await _client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_reports_and_serves_the_authenticated_users_avatar()
    {
        var (user, tokens) = await RegisterAsync();
        byte[] avatar = [0xFF, 0xD8, 0xFF, 0xE0];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
            await db.Users.Where(candidate => candidate.Email == user.Email)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.AvatarImage, avatar)
                    .SetProperty(candidate => candidate.AvatarContentType, "image/jpeg"));
        }

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await _client.GetFromJsonAsync<MeResponse>("/api/v1/me");
        Assert.NotNull(me);
        Assert.True(me.HasAvatar);

        var response = await _client.GetAsync("/api/v1/me/avatar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl?.MaxAge);
        Assert.Equal(avatar, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Avatar_without_token_returns_401()
    {
        var response = await _client.GetAsync("/api/v1/me/avatar");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_and_unknown_user_are_indistinguishable()
    {
        var (user, _) = await RegisterAsync();

        var wrongPassword = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(user.Email, "definitely-not-the-password"));
        var unknownUser = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest($"ghost-{Guid.NewGuid():N}@example.com", "definitely-not-the-password"));

        // Same status, same (empty) body shape — no account enumeration.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Weak_password_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"weak-{Guid.NewGuid():N}@example.com", "short", "Weak"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_the_whole_family()
    {
        // Grace zero: this test is specifically about the STRICT reuse-detection path. The
        // 60-second grace (RefreshReuseGraceTests) turns an immediate replay into a second
        // rotation on purpose — which is a different, deliberately-tested behaviour, not a
        // regression of this one.
        await using var app = factory.WithWebHostBuilder(b =>
            b.UseSetting("Jwt:RefreshReuseGraceSeconds", "0"));
        var client = app.CreateClient();

        var user = NewUser();
        (await client.PostAsJsonAsync("/api/v1/auth/register", user)).EnsureSuccessStatusCode();
        var first = await (await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(user.Email, user.Password))).Content.ReadFromJsonAsync<AuthResponse>();

        // Legitimate rotation succeeds.
        var rotate = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first!.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var second = await rotate.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(second);
        Assert.NotEqual(first.RefreshToken, second!.RefreshToken);

        // Replaying the already-used token is reuse → 401 …
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // … and the reuse revoked the descendant token too (family revocation).
        var descendant = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_tokens()
    {
        var (_, tokens) = await RegisterAsync();

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var logoutResponse = await _client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Change_password_revokes_old_sessions_and_old_password()
    {
        var (user, tokens) = await RegisterAsync();

        using var change = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest(user.Password, "an-even-longer-new-password")),
        };
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var changeResponse = await _client.SendAsync(change);
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);

        // Pre-change refresh token is dead.
        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // Old password no longer signs in; new one does.
        var oldLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, user.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var newLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, "an-even-longer-new-password"));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Tampered_access_token_is_rejected()
    {
        var (_, tokens) = await RegisterAsync();
        var tampered = tokens.AccessToken[..^4] + "AAAA";

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tampered);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
