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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
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

        public void Reset()
        {
            Calls = 0;
            Requested.Clear();
        }

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

    private sealed class ReferenceCapturingRenderer : IImageRenderer
    {
        public int ReferenceCount { get; private set; }

        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct) =>
            Task.FromResult(SolidPng(64, 64, SKColors.Teal));

        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct) =>
            Task.FromResult(SolidPng(width, height, SKColors.Teal));

        public Task<byte[]> RenderExactAsync(
            Guid userId, string prompt, int width, int height, string? modelAlias,
            IReadOnlyList<ImageReference> references, CancellationToken ct)
        {
            ReferenceCount = references.Count;
            return Task.FromResult(SolidPng(width, height, SKColors.Teal));
        }
    }

    private sealed class FixedReferenceResolver : IImageReferenceResolver
    {
        public Task<IReadOnlyList<ImageReference>> ResolveAsync(
            Castmill.Core.Campaign campaign, Castmill.Core.ImageSlot slot, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImageReference>>(
                [new ImageReference(Guid.NewGuid(), "product.png", "image/png", SolidPng(16, 16, SKColors.Blue), "product")]);
    }

    /// <summary>Keeps bytes so ReadAsync can serve the compositor its base image.</summary>
    private sealed class MemoryPublicStore : IPublicContentStore
    {
        public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();
        public bool IsConfigured => true;

        public Task DeleteAsync(string path, CancellationToken ct)
        {
            Blobs.TryRemove(path, out _);
            return Task.CompletedTask;
        }

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
    public async Task Real_reference_images_travel_from_the_item_card_to_the_renderer()
    {
        var renderer = new ReferenceCapturingRenderer();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => renderer));
            s.Replace(ServiceDescriptor.Scoped<IImageReferenceResolver>(_ => new FixedReferenceResolver()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(new MemoryPublicStore()));
        }));
        var client = await AuthedClientAsync(app);
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("References", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var artifact = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("social-x", "A post", """{"content":{"text":"A product post"}}""")))
            .Content.ReadFromJsonAsync<ArtifactResponse>())!;
        var slot = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/image-slots",
            new ImageSlotCreateRequest(artifact.Id, "A faithful product image", "Manual")))
            .Content.ReadFromJsonAsync<ImageSlotResponse>())!;
        var emptyManual = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/image-slots",
            new ImageSlotCreateRequest(artifact.Id, null, "Manual")))
            .Content.ReadFromJsonAsync<ImageSlotResponse>())!;

        var refused = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{emptyManual.Id}/generate", new { variants = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var generated = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}/generate", new { variants = 1 });

        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Equal(1, renderer.ReferenceCount);
    }

    [Fact]
    public async Task A_card_model_override_can_be_cleared_back_to_the_workspace_default()
    {
        await using var app = factory.WithWebHostBuilder(_ => { });
        var client = await AuthedClientAsync(app);
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Model inheritance", null)))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;
        var artifact = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("social-x", "Model test", """{"content":{"text":"Test"}}""")))
            .Content.ReadFromJsonAsync<ArtifactResponse>())!;
        var slot = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots",
            new ImageSlotCreateRequest(artifact.Id)))
            .Content.ReadFromJsonAsync<ImageSlotResponse>())!;

        var overridden = await (await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}",
            new { modelAlias = "image-alt" }))
            .Content.ReadFromJsonAsync<ImageSlotResponse>();
        Assert.Equal("image-alt", overridden!.ModelAlias);

        var inherited = await (await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}",
            new { useDefaultModel = true }))
            .Content.ReadFromJsonAsync<ImageSlotResponse>();
        Assert.Null(inherited!.ModelAlias);

        var ambiguous = await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}",
            new { modelAlias = "image", useDefaultModel = true });
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
    }

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

        // Blog imagery is scoped to ONE blog, so the plan comes in two halves: the
        // campaign-wide slots, plus a per-artifact set for each blog. Reserving without an
        // artifact must NOT create blog slots belonging to no blog.
        var reserved = await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>();
        Assert.Equal(2, reserved!.Count);
        Assert.All(reserved, s => Assert.Null(s.ArtifactId));

        var blogId = await CreateBlogAsync(client, campaign.Id);
        var blogSlots = await (await client.PostAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/reserve?artifactId={blogId}", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>();
        Assert.Equal(4, blogSlots!.Count);
        Assert.All(blogSlots, s => Assert.Equal(blogId, s.ArtifactId));

        // Reservation is idempotent: repeating both calls still leaves exactly six slots.
        await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null);
        await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve?artifactId={blogId}", null);
        var slots = (await client.GetFromJsonAsync<List<ImageSlotResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/image-slots"))!;
        Assert.Equal(6, slots.Count);
        Assert.All(slots, s => Assert.Equal("Empty", s.State));

        var thumb = slots.Single(s => s.Kind == "youtube-thumbnail");
        Assert.Equal(1280, thumb.TargetWidth);
        Assert.Equal(720, thumb.TargetHeight);

        // Auto mode can build a prompt from the owning context even when no manual prompt was
        // saved. This legacy campaign-wide slot falls back to its typed slot label.
        var noPrompt = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/{thumb.Id}/generate", new { variants = 2 });
        Assert.Equal(HttpStatusCode.OK, noPrompt.StatusCode);
        renderer.Reset();

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
        var blog = await CreateBlogAsync(client, campaign.Id);
        var slots = (await (await client.PostAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/reserve?artifactId={blog}", null))
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
        var client = await AuthedClientAsync(factory);

        var status = await client.GetFromJsonAsync<Castmill.Core.Ai.AiStatusResponse>("/api/v1/ai/status");

        var foundry = Assert.Single(status!.ImageProviders, p => p.Name == "foundry");
        Assert.False(foundry.Ready); // test factory blanks the Ai config
        Assert.False(string.IsNullOrWhiteSpace(foundry.Reason));

        // The shipped alternates are listed without any config, and each names the credential
        // slot to fill — a reason a producer can act on, not "not configured".
        var nano = Assert.Single(status.ImageProviders, p => p.Name == "nano-banana");
        Assert.False(nano.Ready);
        Assert.Contains("NanoBananaKey", nano.Reason!, StringComparison.Ordinal);

        var gpt = Assert.Single(status.ImageProviders, p => p.Name == "gpt-image");
        Assert.False(gpt.Ready);
        Assert.Contains("OpenAiImageKey", gpt.Reason!, StringComparison.Ordinal);
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

    /// <summary>
    /// A client that interpolates an unset id sends "?artifactId=", which minimal-API binding
    /// turns into a thrown BadHttpRequestException for a Guid? parameter. It plainly means
    /// "the campaign-wide set", and must not be an exception in the log.
    /// </summary>
    [Fact]
    public async Task An_empty_artifact_id_reserves_the_campaign_wide_set_rather_than_throwing()
    {
        var client = await AuthedClientAsync(factory);
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Empty id", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;

        var response = await client.PostAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/reserve?artifactId=", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var slots = (await response.Content.ReadFromJsonAsync<List<ImageSlotResponse>>())!;
        Assert.Equal(2, slots.Count);
        Assert.All(slots, s => Assert.Null(s.ArtifactId));

        // A non-empty value that is not a GUID is a client error, reported as one.
        var garbage = await client.PostAsync(
            $"/api/v1/campaigns/{campaign.Id}/image-slots/reserve?artifactId=nonsense", null);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    /// <summary>
    /// Generated images kept arriving with their text clipped along the top or the left. The
    /// cause is structural, not stylistic: the model emits its own fixed size and the renderer
    /// then CROPS that to the slot's dimensions, which are a different aspect ratio — so
    /// anything the model placed near an edge is inside the strip that gets cut away.
    ///
    /// The guardrails therefore have to reach every generation path, and have to come LAST so
    /// a brand style block or a user adjustment cannot override them.
    /// </summary>
    [Fact]
    public void Every_image_prompt_carries_the_safe_area_typography_rules_last()
    {
        var slot = new Castmill.Core.ImageSlot
        {
            Kind = "youtube-thumbnail",
            State = "Empty",
            TargetWidth = 1280,
            TargetHeight = 720,
        };
        var plain = Castmill.Api.Endpoints.ImageSlotEndpoints.AppendSlotCompositionGuardrails(
            Castmill.Api.Endpoints.ImageSlotEndpoints.ComposeEffectivePrompt("a hero image", null, null), slot);
        Assert.Contains("1280×720", plain, StringComparison.Ordinal);
        Assert.Contains("16:9", plain, StringComparison.Ordinal);
        Assert.Contains("middle 76%", plain, StringComparison.Ordinal);
        Assert.Contains("No partial", plain, StringComparison.Ordinal);

        // Brand style and a steering note must not be able to have the last word.
        var steered = Castmill.Api.Endpoints.ImageSlotEndpoints.AppendSlotCompositionGuardrails(
            Castmill.Api.Endpoints.ImageSlotEndpoints.ComposeEffectivePrompt(
                "a hero image", "Brand style: terracotta and ink.", "make the text touch the edges"), slot);

        Assert.EndsWith(Castmill.Api.Endpoints.ImageSlotEndpoints.TypographyGuardrails, steered, StringComparison.Ordinal);
        Assert.True(
            steered.IndexOf("Adjustment:", StringComparison.Ordinal)
            < steered.IndexOf("Text rendering rules", StringComparison.Ordinal),
            "The typography rules must be the most recent instruction the model reads.");
    }

    /// <summary>
    /// The thumbnail is the SEO surface YouTube actually shows, so text in it should carry
    /// the campaign's primary keyword — as phrasing, never as a painted keyword list. Riding
    /// on ComposeEffectivePrompt puts it on every slot generation, after the brand style and
    /// before the user's adjustment.
    /// </summary>
    [Fact]
    public void The_primary_keyword_steers_image_text_without_being_stuffed()
    {
        var prompt = Castmill.Api.Endpoints.ImageSlotEndpoints.ComposeEffectivePrompt(
            "a bold thumbnail", "Brand style: terracotta.", "warmer light", "react data grid");

        Assert.Contains("\"react data grid\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Never render a list of keywords", prompt, StringComparison.Ordinal);
        // The user's adjustment stays the LAST word.
        Assert.True(prompt.IndexOf("react data grid", StringComparison.Ordinal)
            < prompt.IndexOf("Adjustment:", StringComparison.Ordinal));

        // And a campaign without targets composes exactly as before.
        Assert.DoesNotContain("keyword", Castmill.Api.Endpoints.ImageSlotEndpoints
            .ComposeEffectivePrompt("a bold thumbnail", null, null, null), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Blog image slots hang off a specific blog, so a test that wants them needs a blog to
    /// hang them from — reserving campaign-wide no longer creates any.
    /// </summary>
    private static async Task<Guid> CreateBlogAsync(HttpClient client, Guid campaignId)
    {
        var blog = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest("blog", "Draft",
                """{"content":{"title":"Draft","markdown":"Intro"},"validation":{}}"""))).Content
            .ReadFromJsonAsync<ArtifactResponse>())!;
        return blog.Id;
    }
}
