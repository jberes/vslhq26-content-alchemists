using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Data;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class VideoReferenceEndpointsTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Reference_set_round_trips_lossless_source_crop_and_brand_library_links()
    {
        var client = await AuthedClientAsync();
        var brand = await CreatedAsync<BrandProfileDetailResponse>(client, "/api/v1/brands",
            new BrandProfileUpsertRequest("Reference test brand", null));
        var campaign = await CreatedAsync<CampaignResponse>(client, "/api/v1/campaigns",
            new CampaignCreateRequest("Video references", null, brand.Id));

        var transcript = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts",
            new TranscriptIngestRequest("A data grid demonstration with timed source evidence.", "accessibility.mp4",
                [new TranscriptSegment("s01", 0, 2, "HOST", "A data grid demonstration.")],
                "/videos/accessibility.mp4", "sha256:video"));
        Assert.Equal(HttpStatusCode.Created, transcript.StatusCode);
        var sources = await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/sources");
        var source = Assert.Single(sources!);
        Assert.Equal(SourceModalities.Media, source.Modality);

        var full = await CreatedAsync<AssetResponse>(client, "/api/v1/assets",
            new AssetCreateRequest("frame-source.png", "image/png", 400_000));
        var cropped = await CreatedAsync<AssetResponse>(client, "/api/v1/assets",
            new AssetCreateRequest("frame-crop.png", "image/png", 250_000));
        var create = new VideoReferenceSetCreateRequest(source.Id, brand.Id, "Five-minute demo", "UI states",
            "ui-demonstration", 293_866, 3840, 2160, 30,
            [new VideoReferenceImageCreateRequest(full.Id, cropped.Id, 18_200, 546,
                new VideoReferenceCropDto(0, 180, 3840, 1980, "automatic", .9),
                "ai-ranked", "0123456789abcdef", 91, "Clear focused grid", "Grid focus", 0)]);

        var createdResponse = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/reference-sets", create);
        Assert.True(createdResponse.StatusCode == HttpStatusCode.Created,
            $"Expected Created, received {createdResponse.StatusCode}: {await createdResponse.Content.ReadAsStringAsync()}");
        var created = (await createdResponse.Content.ReadFromJsonAsync<VideoReferenceSetResponse>())!;
        var image = Assert.Single(created.Images);
        Assert.Equal(source.Id, created.VideoAssetId);
        Assert.Equal(18_200, image.SourceTimestampMs);
        Assert.Equal(full.Id, image.SourceFrameAssetId);
        Assert.Equal(cropped.Id, image.DerivedAssetId);
        Assert.Equal("automatic", image.Crop.Method);

        var listed = await client.GetFromJsonAsync<List<VideoReferenceSetResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/reference-sets?videoAssetId={source.Id}");
        Assert.Single(listed!);

        using (var scope = factory.CreateDbScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
            Assert.True(await db.BrandAssets.IgnoreQueryFilters().AnyAsync(link =>
                link.BrandId == brand.Id && link.AssetId == cropped.Id && link.Kind == "product"));
        }

        var restore = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/reference-images/{image.Id}/restore-full", new { });
        restore.EnsureSuccessStatusCode();
        var restored = (await restore.Content.ReadFromJsonAsync<VideoReferenceImageResponse>())!;
        Assert.Equal(full.Id, restored.DerivedAssetId);
        Assert.Equal("none", restored.Crop.Method);
        Assert.Equal(new[] { 0, 0, 3840, 2160 },
            new[] { restored.Crop.X, restored.Crop.Y, restored.Crop.Width, restored.Crop.Height });

        using (var scope = factory.CreateDbScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
            Assert.True(await db.BrandAssets.IgnoreQueryFilters().AnyAsync(link =>
                link.BrandId == brand.Id && link.AssetId == full.Id));
            Assert.True(await db.BrandAssets.IgnoreQueryFilters().AnyAsync(link =>
                link.BrandId == brand.Id && link.AssetId == cropped.Id));
        }
    }

    [Fact]
    public async Task Crop_presets_are_saved_per_brand_and_invalid_crops_are_rejected()
    {
        var client = await AuthedClientAsync();
        var brand = await CreatedAsync<BrandProfileDetailResponse>(client, "/api/v1/brands",
            new BrandProfileUpsertRequest("Crop preset brand", null));

        var invalid = await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/reference-crop-presets/",
            new ReferenceCropPresetRequest("Outside", 1920, 1080,
                new VideoReferenceCropDto(1900, 0, 100, 1080, "manual"), "browser"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var saved = await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/reference-crop-presets/",
            new ReferenceCropPresetRequest("Browser content", 1920, 1080,
                new VideoReferenceCropDto(0, 90, 1920, 990, "manual"), "browser"));
        saved.EnsureSuccessStatusCode();
        var presets = await client.GetFromJsonAsync<List<ReferenceCropPresetResponse>>(
            $"/api/v1/brands/{brand.Id}/reference-crop-presets/");
        var preset = Assert.Single(presets!);
        Assert.Equal("Browser content", preset.Name);
        Assert.Equal(90, preset.Crop.Y);
    }

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"reference-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple", "Reference Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<T> CreatedAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
