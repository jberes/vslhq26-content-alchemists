using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// Items 4/11 of the UX overhaul: takes persist as ImageVariant rows with thumbnails,
/// generation reports through a pollable image-kind run, keep/discard is a state flip,
/// steer creates a lineage-linked take, and placing by id fills the slot.
/// </summary>
[Collection("api")]
public sealed class ImageVariantTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Generate_persists_variants_with_thumbs_and_a_pollable_image_run()
    {
        var (client, campaignId, slotId) = await SetUpSlotAsync();

        var generated = await Client(client).PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 2 });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var batch = (await generated.Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        Assert.Equal(2, batch.Variants.Count);
        Assert.All(batch.Variants, v =>
        {
            Assert.Equal("Candidate", v.State);
            Assert.Contains("/thumbs/", v.ThumbUrl, StringComparison.Ordinal);
            Assert.NotEqual(v.Url, v.ThumbUrl);
        });

        // The run row is image-kind and completed; runs/latest default (content) ignores it.
        var run = await client.GetStringAsync($"/api/v1/ai/runs/{batch.RunId}");
        Assert.Contains("\"status\":\"Completed\"", run, StringComparison.OrdinalIgnoreCase);
        var latestContent = await client.GetAsync($"/api/v1/ai/campaigns/{campaignId}/runs/latest");
        Assert.Equal(HttpStatusCode.NotFound, latestContent.StatusCode);
        var latestImage = await client.GetAsync($"/api/v1/ai/campaigns/{campaignId}/runs/latest?kind=image");
        Assert.Equal(HttpStatusCode.OK, latestImage.StatusCode);

        // The gallery lists the persisted takes.
        var listed = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants");
        Assert.Equal(2, listed!.Count);
    }

    [Fact]
    public async Task Keep_discard_is_a_state_flip_and_discarded_hide_by_default()
    {
        var (client, campaignId, slotId) = await SetUpSlotAsync();
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 2 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var baseUrl = $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants";

        var kept = await Patch(client, $"{baseUrl}/{batch.Variants[0].Id}", new { state = "Kept" });
        Assert.Equal("Kept", kept.State);
        var discarded = await Patch(client, $"{baseUrl}/{batch.Variants[1].Id}", new { state = "Discarded" });
        Assert.Equal("Discarded", discarded.State);

        var visible = await client.GetFromJsonAsync<List<ImageVariantResponse>>(baseUrl);
        Assert.Single(visible!);
        var all = await client.GetFromJsonAsync<List<ImageVariantResponse>>($"{baseUrl}?includeDiscarded=true");
        Assert.Equal(2, all!.Count);

        var invalid = await client.PatchAsync($"{baseUrl}/{batch.Variants[0].Id}",
            JsonContent.Create(new { state = "Vanished" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Steer_creates_a_lineage_linked_take_with_the_adjusted_prompt()
    {
        var renderer = new CapturingRenderer();
        var (client, campaignId, slotId) = await SetUpSlotAsync(renderer);
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var source = batch.Variants[0];

        var steered = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{source.Id}/steer",
            new { note = "add the host's face on the right" }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        var take = Assert.Single(steered.Variants);
        Assert.Equal(source.Id, take.SourceVariantId);
        Assert.Equal("add the host's face on the right", take.SteeringNote);
        Assert.Contains("Adjustment: add the host's face on the right",
            renderer.Prompts.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Place_by_variant_id_fills_the_slot_and_marks_the_take_kept()
    {
        var (client, campaignId, slotId) = await SetUpSlotAsync();
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        var placed = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/place",
            new { variantId = batch.Variants[0].Id });
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        Assert.Contains("\"state\":\"Filled\"", await placed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var takes = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants");
        Assert.Equal("Kept", Assert.Single(takes!).State);

        // A foreign variant id is a plain 404.
        var foreign = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/place",
            new { variantId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Brand_image_style_reaches_the_render_prompt()
    {
        var renderer = new CapturingRenderer();
        var (client, campaignId, slotId) = await SetUpSlotAsync(renderer, async c =>
        {
            var brand = (await (await c.PostAsJsonAsync("/api/v1/brands", new BrandProfileUpsertRequest(
                "Acme", new BrandStyleCard(
                    ImageStyle: "Clean editorial photography, muted blues",
                    Colors: [new BrandColor("primary", "#0A66C2")]))))
                .Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
            return brand.Id;
        });

        (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .EnsureSuccessStatusCode();

        var prompt = renderer.Prompts.Last();
        Assert.Contains("Clean editorial photography, muted blues", prompt, StringComparison.Ordinal);
        Assert.Contains("#0A66C2", prompt, StringComparison.Ordinal);

        // The exact prompt is persisted on the take — reproducibility.
        var takes = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants");
        Assert.Single(takes!);
    }

    // ---- Setup ------------------------------------------------------------------

    private async Task<(HttpClient Client, Guid CampaignId, Guid SlotId)> SetUpSlotAsync(
        CapturingRenderer? renderer = null,
        Func<HttpClient, Task<Guid>>? brandFactory = null)
    {
        var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => renderer ?? new CapturingRenderer()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(new MemoryPublicStore()));
        }));

        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"var-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Variant Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        Guid? brandId = brandFactory is null ? null : await brandFactory(client);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Takes", null, brandId))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var slots = (await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>())!;
        var slot = slots.Single(s => s.Kind == "youtube-thumbnail");

        (await client.PatchAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}",
            JsonContent.Create(new { prompt = "a bold thumbnail" }))).EnsureSuccessStatusCode();

        return (client, campaign.Id, slot.Id);
    }

    private static HttpClient Client(HttpClient client) => client;

    private static async Task<ImageVariantResponse> Patch(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsync(url, JsonContent.Create(body));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImageVariantResponse>())!;
    }

    private sealed class CapturingRenderer : IImageRenderer
    {
        public List<string> Prompts { get; } = [];

        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(SolidPng(64, 64));
        }

        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(SolidPng(1024, 1024));
        }

        private static byte[] SolidPng(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap);
            return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }
    }

    private sealed class MemoryPublicStore : IPublicContentStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

        public bool IsConfigured => true;

        public Task DeleteAsync(string path, CancellationToken ct)
        {
            _blobs.TryRemove(path, out _);
            return Task.CompletedTask;
        }

        public Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct)
        {
            _blobs[path] = bytes.ToArray();
            return Task.FromResult(new Uri($"https://public.example/{path}"));
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
            Task.FromResult(_blobs.TryGetValue(path, out var bytes) ? bytes : null);
    }
}
