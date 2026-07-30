using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Images;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>B9.1–B9.3 + B9.5: typed image slots, slot-accurate output, headline compositing.</summary>
[Collection("api")]
public sealed class ImagePlanTests(CastmillApiFactory factory)
{
    // ---- Unit: crop geometry + compositing ------------------------------------

    private static byte[] SolidPng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Theory]
    [InlineData(1024, 1024, 1280, 720)]   // square model output → 16:9 thumbnail
    [InlineData(1536, 1024, 1200, 1200)]  // landscape output → square social card
    [InlineData(1024, 1536, 1600, 840)]   // portrait output → wide blog header
    public void Model_output_is_cropped_to_the_slots_exact_dimensions(int srcW, int srcH, int outW, int outH)
    {
        var composer = NewComposer();
        var webp = composer.ToSlotWebp(SolidPng(srcW, srcH, SKColors.CornflowerBlue), outW, outH);

        using var decoded = SKBitmap.Decode(webp);
        Assert.Equal(outW, decoded.Width);
        Assert.Equal(outH, decoded.Height);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(webp, 0, 4));
    }

    [Fact]
    public void Centre_crop_covers_the_frame_without_squashing()
    {
        // A 2:1 source cropped to 1:1 must lose width, not compress it: the scale
        // factor is uniform, so the centre pixel column stays the centre column.
        using var source = new SKBitmap(400, 200);
        source.Erase(SKColors.Black);
        for (var y = 0; y < 200; y++)
        {
            source.SetPixel(200, y, SKColors.Red); // centre column marker
        }

        using var cropped = ImageComposer.CentreCrop(source, 200, 200);
        Assert.Equal(200, cropped.Width);
        Assert.Equal(200, cropped.Height);
        Assert.Equal(SKColors.Red, cropped.GetPixel(100, 100)); // marker still centred
    }

    [Fact]
    public void Headline_is_composited_and_font_fallback_is_reported()
    {
        var composer = NewComposer();
        var slotImage = composer.ToSlotWebp(SolidPng(1024, 1024, SKColors.Black), 1280, 720);

        var result = composer.ComposeHeadline(slotImage, "Shipping Blazor at Scale", safeArea: true);

        using var decoded = SKBitmap.Decode(result.Image);
        Assert.Equal(1280, decoded.Width);
        Assert.Equal(720, decoded.Height);
        // The overlay actually drew: some pixel in the lower safe band is no longer black.
        var drew = false;
        for (var x = 0; x < 1280 && !drew; x += 4)
        {
            for (var y = 600; y < 700; y += 4)
            {
                if (decoded.GetPixel(x, y) != SKColors.Black)
                {
                    drew = true;
                    break;
                }
            }
        }
        Assert.True(drew, "headline pixels were not composited into the safe area");
        // Barlow Condensed now ships with the API (Assets/Fonts), so the compositor must
        // resolve a real embedded face rather than falling back to a platform font — the
        // whole point of ADR-013 is not depending on system fonts on App Service.
        Assert.False(result.FontFallback);
        Assert.Contains("Barlow", result.Typeface, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_headline_shrinks_instead_of_being_clipped()
    {
        var composer = NewComposer();
        var slotImage = composer.ToSlotWebp(SolidPng(1024, 1024, SKColors.Black), 1280, 720);
        // 32 chars is the field cap; it must still fit inside the safe area.
        var result = composer.ComposeHeadline(slotImage, new string('W', 32), safeArea: true);
        using var decoded = SKBitmap.Decode(result.Image);
        Assert.Equal(1280, decoded.Width);
    }

    private static ImageComposer NewComposer() => new(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageComposer>.Instance);

    // ---- Integration fakes -----------------------------------------------------

    /// <summary>Returns real decodable bytes so the crop + composite path is exercised end to end.</summary>
    private sealed class PngRenderer : IImageRenderer
    {
        public int Calls { get; private set; }
        public List<(int Width, int Height)> Requested { get; } = [];

        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct) =>
            Task.FromResult(SolidPng(64, 64, SKColors.Teal));

        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct)
        {
            Calls++;
            Requested.Add((width, height));
            // Emit at a model-native size; the endpoint owns the crop to slot size.
            return Task.FromResult(SolidPng(1024, 1024, SKColors.Teal));
        }
    }

    /// <summary>Keeps bytes so ReadAsync can serve the compositor its base image.</summary>
    private sealed class MemoryPublicStore : IPublicContentStore
    {
        public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();
        public bool IsConfigured => true;

        public Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct)
        {
            Blobs[path] = bytes.ToArray();
            return Task.FromResult(new Uri($"https://public.example/{path}"));
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
            Task.FromResult(Blobs.TryGetValue(path, out var bytes) ? bytes : null);
    }

    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"slot-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Slot Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    // ---- Integration: the plan's whole lifecycle -------------------------------

    [Fact]
    public async Task Slot_lifecycle_reserve_prompt_generate_place_and_composite()
    {
        var renderer = new PngRenderer();
        var store = new MemoryPublicStore();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => renderer));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(store));
        }));
        var client = await AuthedClientAsync(app);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Slots", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;

        // Reservation is idempotent: two calls still leave exactly six slots.
        var reserved = await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>();
        await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null);
        var slots = (await client.GetFromJsonAsync<List<ImageSlotResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/image-slots"))!;
        Assert.Equal(6, reserved!.Count);
        Assert.Equal(6, slots.Count);
        Assert.All(slots, s => Assert.Equal("Empty", s.State));

        var thumb = slots.Single(s => s.Kind == "youtube-thumbnail");
        Assert.Equal(1280, thumb.TargetWidth);
        Assert.Equal(720, thumb.TargetHeight);

        // A slot with no prompt refuses to generate rather than burning a call.
        var noPrompt = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{thumb.Id}/generate", new { variants = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, noPrompt.StatusCode);

        var patched = await (await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{thumb.Id}",
            new { prompt = "a bold thumbnail", sourceSegmentId = "s02", headlineText = "Trust is not cheap" }))
            .Content.ReadFromJsonAsync<ImageSlotResponse>();
        Assert.Equal("s02", patched!.SourceSegmentId);
        Assert.Equal("Trust is not cheap", patched.HeadlineText);

        // Generate variants at the slot's exact dimensions.
        var generated = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{thumb.Id}/generate", new { variants = 3 });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        using var genDoc = JsonDocument.Parse(await generated.Content.ReadAsStringAsync());
        var variants = genDoc.RootElement.GetProperty("variants");
        Assert.Equal(3, variants.GetArrayLength());
        Assert.Equal(3, renderer.Calls);
        Assert.All(renderer.Requested, r => Assert.Equal((1280, 720), r));
        // Every variant is a distinct blob: immutable cache headers forbid reuse.
        var urls = variants.EnumerateArray().Select(v => v.GetProperty("url").GetString()!).ToList();
        Assert.Equal(3, urls.Distinct(StringComparer.Ordinal).Count());

        // Seed a blog with the matching stub so placing rewrites the manuscript.
        var blog = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Draft",
                """{"content":{"title":"Draft","markdown":"Intro ![stub:youtube-thumbnail]() end"},"validation":{}}"""))).Content
            .ReadFromJsonAsync<ArtifactResponse>())!;

        var placed = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{thumb.Id}/place",
            new { url = urls[1], blogArtifactId = blog.Id });
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        using var placeDoc = JsonDocument.Parse(await placed.Content.ReadAsStringAsync());
        var slotAfterPlace = placeDoc.RootElement.GetProperty("slot");
        Assert.Equal("Filled", slotAfterPlace.GetProperty("state").GetString());
        // Headline present → the published image is the composited one, not the base.
        var compositedUrl = slotAfterPlace.GetProperty("publishedUrl").GetString()!;
        Assert.NotEqual(urls[1], compositedUrl);
        Assert.Equal(urls[1], slotAfterPlace.GetProperty("baseImageUrl").GetString());
        Assert.Contains("composited/", compositedUrl, StringComparison.Ordinal);

        // The manuscript stub is gone and the artifact took a revision.
        var updatedBlog = (await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{blog.Id}"))!;
        Assert.DoesNotContain("![stub:youtube-thumbnail]()", updatedBlog.ContentJson, StringComparison.Ordinal);
        Assert.Equal(2, updatedBlog.Version);
        var revisions = (await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{blog.Id}/revisions"))!;
        Assert.Equal("image-placed", Assert.Single(revisions).Reason);

        // The campaign preview — one payload for every image counter (G9).
        using var preview = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/campaigns/{campaign.Id}/preview"));
        Assert.Equal(1, preview.RootElement.GetProperty("imagesFilled").GetInt32());
        Assert.Equal(6, preview.RootElement.GetProperty("imagesTotal").GetInt32());

        // Re-composite after a headline edit: no new model call.
        var callsBefore = renderer.Calls;
        var recomposited = await client.PostAsJsonAsync("/api/v1/images/composite",
            new { campaignId = campaign.Id, slotId = thumb.Id, headline = "Generation is cheap", safeArea = true });
        Assert.Equal(HttpStatusCode.OK, recomposited.StatusCode);
        using var recompDoc = JsonDocument.Parse(await recomposited.Content.ReadAsStringAsync());
        Assert.Equal(callsBefore, renderer.Calls);
        Assert.NotEqual(compositedUrl, recompDoc.RootElement.GetProperty("slot").GetProperty("publishedUrl").GetString());
        Assert.False(recompDoc.RootElement.GetProperty("fontFallback").GetBoolean());

        // Clearing resets state but keeps the user's prompt.
        var cleared = await (await client.DeleteAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/{thumb.Id}"))
            .Content.ReadFromJsonAsync<ImageSlotResponse>();
        Assert.Equal("Empty", cleared!.State);
        Assert.Null(cleared.PublishedUrl);
        Assert.Equal("a bold thumbnail", cleared.Prompt);
    }

    [Fact]
    public async Task Placing_a_url_that_is_not_this_slots_variant_is_rejected()
    {
        var store = new MemoryPublicStore();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => new PngRenderer()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(store));
        }));
        var client = await AuthedClientAsync(app);
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Slots", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var slots = (await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>())!;
        var slot = slots.Single(s => s.Kind == "blog-header");

        // Arbitrary external content may not be pointed at a slot.
        var foreign = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}/place",
            new { url = "https://evil.example/anything.webp" });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // Nor may another slot's variant.
        var otherSlotUrl = $"https://public.example/campaigns/{campaign.Id}/images/social-card/variants/1-abc.webp";
        var wrongSlot = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}/place", new { url = otherSlotUrl });
        Assert.Equal(HttpStatusCode.BadRequest, wrongSlot.StatusCode);
    }

    [Fact]
    public void Image_prompt_artifacts_map_onto_the_reserved_slots()
    {
        var prompts = ImagePlanService.ParsePrompts(
            """
            {"content":{"title":"Images","citations":["s01"],
              "images":[{"slot":"blog-hero","prompt":"hero shot","aspectRatio":"16:9"},
                        {"slot":"youtube-thumbnail","prompt":"bold thumb","segmentId":"s04"}]}}
            """);
        Assert.Equal("hero shot", prompts["blog-hero"].Prompt);
        // No segmentId on the image → falls back to the artifact's first citation.
        Assert.Equal("s01", prompts["blog-hero"].SegmentId);
        Assert.Equal("s04", prompts["youtube-thumbnail"].SegmentId);
        // The generator's vocabulary maps onto the plan's kinds.
        Assert.Equal("blog-hero", ImagePlanService.MapPromptSlot("blog-header"));
    }

    [Fact]
    public async Task Status_reports_image_provider_readiness_with_reasons()
    {
        await using var app = factory.WithWebHostBuilder(b =>
        {
            // An enabled non-Foundry provider (ADR-015) with no stored key.
            b.UseSetting("Ai:Providers:nano-banana:Enabled", "true");
            b.UseSetting("Ai:Providers:nano-banana:Endpoint", "https://images.example/v1");
            b.UseSetting("Ai:Providers:nano-banana:Model", "nano-banana-1");
        });
        var client = await AuthedClientAsync(app);

        var status = await client.GetFromJsonAsync<Castmill.Core.Ai.AiStatusResponse>("/api/v1/ai/status");

        var foundry = Assert.Single(status!.ImageProviders, p => p.Name == "foundry");
        Assert.False(foundry.Ready); // test factory blanks the Ai config
        Assert.False(string.IsNullOrWhiteSpace(foundry.Reason));

        var external = Assert.Single(status.ImageProviders, p => p.Name == "nano-banana");
        Assert.False(external.Ready);
        Assert.Contains("ImageProviderKey", external.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_providers_are_not_resolvable()
    {
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Ai:Providers:off-by-default:Enabled", "false");
            b.UseSetting("Ai:Providers:off-by-default:Endpoint", "https://images.example/v1");
        });
        var client = await AuthedClientAsync(app);
        var status = await client.GetFromJsonAsync<Castmill.Core.Ai.AiStatusResponse>("/api/v1/ai/status");

        // A provider that isn't enabled never reaches the registry at all.
        Assert.DoesNotContain(status!.ImageProviders, p => p.Name == "off-by-default");
        Assert.Contains(status.ImageProviders, p => p.Name == "foundry");
    }
}
