using System.Net;
using System.Net.Http.Json;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Castmill.Api.Tests;

/// <summary>
/// Uses its own factory (no DB dependency — the limiter rejects before the
/// endpoint runs) so the tiny limit can't interfere with the functional suite.
/// </summary>
public sealed class RateLimitTests
{
    private sealed class TinyLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Jwt:SigningKey", new string('k', 48));
            builder.UseSetting("Castmill:EncryptionKey",
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("RateLimits:AuthPerMinute", "3");
        }
    }

    [Fact]
    public async Task Auth_endpoints_return_429_after_the_window_limit()
    {
        await using var factory = new TinyLimitFactory();
        var client = factory.CreateClient();
        var body = new LoginRequest("nobody@example.com", "irrelevant-password");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", body);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        // The first requests inside the window were not rate-limited.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, statuses[0]);
    }

    [Fact]
    public async Task Startup_refuses_missing_signing_key()
    {
        await using var factory = new WebApplicationFactory<Program>();
        // No Jwt:SigningKey anywhere (user-secrets are not loaded outside Development):
        // building the host must fail loudly rather than boot insecurely.
        var ex = Record.Exception(() => factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Staging");
            b.UseSetting("Jwt:SigningKey", "");
        }).CreateClient());
        Assert.NotNull(ex);
        Assert.Contains("Jwt:SigningKey", ex.Message, StringComparison.Ordinal);
    }
}
