using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ResourceApiTests(CastmillApiFactory factory)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"res-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Resource Tester"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<CampaignResponse> CreateCampaignAsync(HttpClient client, string name = "Launch")
    {
        var response = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest(name, "the brief"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CampaignResponse>())!;
    }

    [Fact]
    public async Task Campaign_full_crud_roundtrip()
    {
        var client = await AuthedClientAsync();

        var created = await CreateCampaignAsync(client, "CRUD campaign");
        Assert.Equal("CRUD campaign", created.Name);

        var updated = await client.PutAsJsonAsync($"/api/v1/campaigns/{created.Id}",
            new CampaignUpdateRequest("Renamed", null));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var list = await client.GetFromJsonAsync<List<CampaignResponse>>("/api/v1/campaigns");
        Assert.Contains(list!, c => c.Id == created.Id && c.Name == "Renamed");

        var delete = await client.DeleteAsync($"/api/v1/campaigns/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var gone = await client.GetAsync($"/api/v1/campaigns/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Campaigns_of_other_tenants_are_a_plain_404_via_the_api()
    {
        var alice = await AuthedClientAsync();
        var bob = await AuthedClientAsync();

        var campaign = await CreateCampaignAsync(alice, "Alice private");

        // Bob can neither read, update, nor delete Alice's campaign — and the
        // response is indistinguishable from a nonexistent id (G1 at HTTP level).
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}",
            new CampaignUpdateRequest("hijack", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);
    }

    [Fact]
    public async Task Artifact_etag_contract_missing_stale_and_current_if_match()
    {
        var client = await AuthedClientAsync();
        var campaign = await CreateCampaignAsync(client);
        var baseUrl = $"/api/v1/campaigns/{campaign.Id}/artifacts";

        var createResponse = await client.PostAsJsonAsync(baseUrl,
            new ArtifactCreateRequest("blog", "Draft", """{"body":"v1"}"""));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var artifact = (await createResponse.Content.ReadFromJsonAsync<ArtifactResponse>())!;
        Assert.Equal("\"1\"", createResponse.Headers.ETag!.Tag);

        // No If-Match → 428 Precondition Required.
        using var noEtag = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/{artifact.Id}")
        {
            Content = JsonContent.Create(new ArtifactUpdateRequest("Draft", """{"body":"v2"}""")),
        };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(noEtag)).StatusCode);

        // Current If-Match → 200, version bumps.
        using var goodEtag = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/{artifact.Id}")
        {
            Content = JsonContent.Create(new ArtifactUpdateRequest("Draft", """{"body":"v2"}""")),
        };
        goodEtag.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        var updateResponse = await client.SendAsync(goodEtag);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal("\"2\"", updateResponse.Headers.ETag!.Tag);

        // Stale If-Match (the B2 deferred gate) → 412 Precondition Failed.
        using var staleEtag = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/{artifact.Id}")
        {
            Content = JsonContent.Create(new ArtifactUpdateRequest("Draft", """{"body":"lost-update"}""")),
        };
        staleEtag.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(staleEtag)).StatusCode);
    }

    [Fact]
    public async Task Artifact_rejects_malformed_json_content()
    {
        var client = await AuthedClientAsync();
        var campaign = await CreateCampaignAsync(client);

        var response = await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Bad", "not json {{{"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preview_projection_for_50_artifacts_stays_under_100KB_and_has_no_content()
    {
        var client = await AuthedClientAsync();
        var campaign = await CreateCampaignAsync(client, "Seeded");
        var baseUrl = $"/api/v1/campaigns/{campaign.Id}/artifacts";

        // ~4 KB of content per artifact: full campaign would be ~200 KB; the
        // preview must strip all of it (check-in gate: < 100 KB for 50).
        var heavyContent = JsonSerializer.Serialize(new { body = new string('x', 4000) });
        for (var i = 0; i < 50; i++)
        {
            var create = await client.PostAsJsonAsync(baseUrl,
                new ArtifactCreateRequest("social", $"Post {i:D2}", heavyContent));
            create.EnsureSuccessStatusCode();
        }

        var listResponse = await client.GetAsync(baseUrl);
        listResponse.EnsureSuccessStatusCode();
        var payload = await listResponse.Content.ReadAsStringAsync();

        Assert.True(payload.Length < 100_000, $"Preview payload was {payload.Length} bytes.");
        Assert.DoesNotContain("xxxx", payload, StringComparison.Ordinal);
        var previews = JsonSerializer.Deserialize<List<ArtifactPreviewResponse>>(payload, WebJson);
        Assert.Equal(50, previews!.Count);
    }

    [Fact]
    public async Task Settings_roundtrip_and_secret_prefix_is_refused()
    {
        var client = await AuthedClientAsync();

        var put = await client.PutAsJsonAsync("/api/v1/settings/ui.theme", new SettingWriteRequest("dark"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var list = await client.GetFromJsonAsync<List<SettingResponse>>("/api/v1/settings");
        Assert.Contains(list!, s => s.Key == "ui.theme" && s.Value == "dark");

        // The reserved prefix is refused until the encrypted store exists (B3).
        var secret = await client.PutAsJsonAsync("/api/v1/settings/secret.foundry-key",
            new SettingWriteRequest("sk-never-store-me"));
        Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
    }

    [Fact]
    public async Task Brands_and_assets_crud_roundtrip()
    {
        var client = await AuthedClientAsync();

        var brand = await client.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Acme", new BrandStyleCard(Voice: "warm, direct")));
        Assert.Equal(HttpStatusCode.Created, brand.StatusCode);
        var brandBody = (await brand.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        Assert.Equal("warm, direct", brandBody.StyleCard?.Voice);

        var asset = await client.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("../../evil path.mp4", "video/mp4", 1024));
        Assert.Equal(HttpStatusCode.Created, asset.StatusCode);
        var assetBody = (await asset.Content.ReadFromJsonAsync<AssetResponse>())!;
        // Server-derived blob path is sanitized — no traversal survives.
        Assert.DoesNotContain("..", assetBody.BlobPath, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", assetBody.BlobPath, StringComparison.Ordinal);

        var assets = await client.GetFromJsonAsync<List<AssetResponse>>("/api/v1/assets");
        Assert.Contains(assets!, a => a.Id == assetBody.Id);
    }
}
