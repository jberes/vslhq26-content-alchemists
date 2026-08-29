using Castmill.Core.Auth;
using Castmill.UI.Auth;
using Castmill.UI.Http;

namespace Castmill.UI.Tests;

public sealed class ExternalAuthClientTests
{
    [Fact]
    public void Pkce_uses_a_32_byte_base64url_verifier_and_s256_challenge()
    {
        var first = Pkce.Create();
        var second = Pkce.Create();

        Assert.Equal(43, first.CodeVerifier.Length);
        Assert.Equal(43, first.CodeChallenge.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.CodeVerifier);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.CodeChallenge);
        Assert.Equal(Pkce.CreateChallenge(first.CodeVerifier), first.CodeChallenge);
        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
    }

    [Fact]
    public async Task Start_resolves_the_serialized_relative_browser_url_against_the_api_origin()
    {
        var handler = new StubHttpHandler();
        var attemptId = Guid.NewGuid();
        handler.OnPost("api/v1/auth/external/start", new ExternalAuthStartResponse(
            attemptId,
            $"/api/v1/auth/external/browser/{attemptId:D}",
            "poll-secret",
            DateTimeOffset.UtcNow.AddMinutes(10)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7105/") };
        var client = new AuthClient(new ApiClient(http), http);

        var result = await client.StartExternalAsync(Request());

        Assert.Equal(
            new Uri($"https://localhost:7105/api/v1/auth/external/browser/{attemptId:D}"),
            result.AbsoluteBrowserUri);
        Assert.DoesNotContain(result.Response.PollSecret, result.AbsoluteBrowserUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_rejects_a_browser_url_on_another_origin()
    {
        var handler = new StubHttpHandler();
        handler.OnPost("api/v1/auth/external/start", new ExternalAuthStartResponse(
            Guid.NewGuid(),
            "https://example.invalid/auth",
            "poll-secret",
            DateTimeOffset.UtcNow.AddMinutes(10)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7105/") };
        var client = new AuthClient(new ApiClient(http), http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartExternalAsync(Request()));

        Assert.Contains("not on the API origin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_reads_the_protected_account_method_projection()
    {
        var handler = new StubHttpHandler();
        handler.OnGet("api/v1/auth/external/links", new ExternalAuthLinksResponse(
            false,
            [
                new(ExternalAuthProviders.Microsoft, true, true),
                new(ExternalAuthProviders.Google, false, false),
            ]));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7105/") };
        var client = new AuthClient(new ApiClient(http), http);

        var result = await client.ExternalLinksAsync();

        Assert.False(result.HasPassword);
        Assert.True(result.Providers.Single(provider =>
            provider.Provider == ExternalAuthProviders.Microsoft).Linked);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath == "/api/v1/auth/external/links");
    }

    [Fact]
    public async Task Unlink_uses_the_protected_provider_route()
    {
        var handler = new StubHttpHandler();
        handler.OnStatus(
            HttpMethod.Delete,
            "api/v1/auth/external/link/google",
            System.Net.HttpStatusCode.NoContent);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7105/") };
        var client = new AuthClient(new ApiClient(http), http);

        await client.UnlinkAsync(ExternalAuthProviders.Google);

        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete
            && request.RequestUri?.AbsolutePath == "/api/v1/auth/external/link/google");
    }

    private static ExternalAuthStartRequest Request() => new(
        ExternalAuthProviders.Microsoft,
        ExternalAuthClientKinds.Desktop,
        ExternalAuthReturnRoutes.SignIn,
        new string('a', 43),
        ExternalAuthCodeChallengeMethods.S256);
}