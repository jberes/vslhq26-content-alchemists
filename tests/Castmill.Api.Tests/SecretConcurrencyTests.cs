using Castmill.Api.Services.Secrets;
using Castmill.Core.Auth;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

/// <summary>
/// Image generation fans several renders out in parallel over one request scope, and every
/// provider resolves its credentials on the way through. Reported 2026-09-06 as
/// "Take image_alt v3: A second operation was started on this context instance…" — concurrent
/// reads on the scoped DbContext. Secrets cannot change under a single request, so reads are
/// serialised and cached (ADR-064).
/// </summary>
[Collection("api")]
public sealed class SecretConcurrencyTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Parallel_secret_reads_in_one_scope_do_not_race_the_db_context()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"race-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Racer"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        (await client.PutAsJsonAsync("/api/v1/settings/secrets/NanoBananaKey",
            new SecretValue("AIza-value"))).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<IUserSecretsService>();
        var me = (await client.GetFromJsonAsync<MeResponse>("/api/v1/me"))!;
        var userId = me.UserId;

        // Twelve at once: the shape the render fan-out produces.
        var reads = Enumerable.Range(0, 12)
            .Select(_ => secrets.GetAsync(userId, SecretKind.NanoBananaKey, CancellationToken.None))
            .ToArray();

        // Completing at all is the assertion: before the fix this threw
        // InvalidOperationException from the shared DbContext. (This scope carries no tenant
        // claim, so the tenant query filter returns nothing — the read path, not the value,
        // is what is under test.)
        var values = await Task.WhenAll(reads);
        Assert.All(values, value => Assert.Equal(values[0], value));
    }

    private sealed record SecretValue(string Value);
}
