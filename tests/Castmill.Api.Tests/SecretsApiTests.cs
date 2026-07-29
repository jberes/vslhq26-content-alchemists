using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class SecretsApiTests(CastmillApiFactory factory)
{
    private const string SecretValue = "sk-super-secret-foundry-key-000";

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"sec-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Secret Tester"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Secret_set_status_and_no_response_ever_contains_the_value()
    {
        var client = await AuthedClientAsync();

        // Set: 204, and the response body must not echo the secret.
        var set = await client.PutAsJsonAsync("/api/v1/settings/secrets/FoundryKey",
            new SecretWriteRequestDto(SecretValue));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.DoesNotContain(SecretValue, await set.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Status: reports configured=true, never the value.
        var status = await client.GetAsync("/api/v1/settings/secrets");
        var statusBody = await status.Content.ReadAsStringAsync();
        Assert.Contains("FoundryKey", statusBody, StringComparison.Ordinal);
        Assert.Contains("true", statusBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretValue, statusBody, StringComparison.Ordinal);

        // The plaintext settings list must not leak encrypted rows either.
        var settingsBody = await (await client.GetAsync("/api/v1/settings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretValue, settingsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.FoundryKey", settingsBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_is_encrypted_at_rest()
    {
        var client = await AuthedClientAsync();
        var marker = $"plaintext-marker-{Guid.NewGuid():N}";
        var set = await client.PutAsJsonAsync("/api/v1/settings/secrets/BrokerToken",
            new SecretWriteRequestDto(marker));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        // Inspect the raw row bypassing the API: value must be ciphertext.
        using var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>();
        await using var db = new CastmillDbContext(options, new NullTenantProvider());
        var row = await db.UserSettings
            .IgnoreQueryFilters()
            .SingleAsync(s => s.Key == "secret.BrokerToken" && s.Value != marker);
        Assert.True(row.IsEncrypted);
        Assert.DoesNotContain(marker, row.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_secret_kind_is_404()
    {
        var client = await AuthedClientAsync();
        var response = await client.PutAsJsonAsync("/api/v1/settings/secrets/NotAKind",
            new SecretWriteRequestDto("x"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Blob_sas_endpoints_scope_to_owned_assets()
    {
        var client = await AuthedClientAsync();
        var asset = await client.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("clip.mp4", "video/mp4", 2048));
        var assetBody = (await asset.Content.ReadFromJsonAsync<AssetResponse>())!;

        var mint = await client.PostAsync($"/api/v1/blob/assets/{assetBody.Id}/upload-sas", null);
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var mintBody = await mint.Content.ReadAsStringAsync();
        Assert.Contains("sig=", mintBody, StringComparison.Ordinal);
        Assert.Contains("sp=cw", mintBody, StringComparison.Ordinal);
        // The account key must never appear in a response.
        Assert.DoesNotContain("AccountKey", mintBody, StringComparison.OrdinalIgnoreCase);

        // Another tenant's asset: plain 404, no SAS minted.
        var stranger = await AuthedClientAsync();
        var denied = await stranger.PostAsync($"/api/v1/blob/assets/{assetBody.Id}/upload-sas", null);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private sealed record SecretWriteRequestDto(string Value);

    private sealed class NullTenantProvider : Castmill.Api.Tenancy.ITenantProvider
    {
        public Guid? TenantId => null;
    }
}
