using Bunit;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Castmill.UI.Pages.Campaign.Bench;

namespace Castmill.UI.Tests;

/// <summary>
/// The create-from-scratch dialog is a complete workflow, not another route into the generated
/// take lightbox. These checks drive it from the studio: open, drop a background, drop an image
/// layer (drawn from the full-resolution original), save the composite, and close with Escape.
/// Editor behaviour in depth lives in ImageBenchTests.
/// </summary>
public sealed class ImageStudioManualEditorTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("d4111111-1111-1111-1111-111111111111");
    private static readonly Guid ArtifactId = Guid.Parse("d4111111-1111-1111-1111-222222222222");
    private static readonly Guid SlotId = Guid.Parse("d4111111-1111-1111-1111-333333333333");
    private static readonly Guid BrandId = Guid.Parse("d4111111-1111-1111-1111-444444444444");
    private static readonly Guid BrandAssetId = Guid.Parse("d4111111-1111-1111-1111-555555555555");
    private static readonly Guid AssetId = Guid.Parse("d4111111-1111-1111-1111-666666666666");

    private readonly BrandAssetResponse _asset = new(
        BrandAssetId, BrandId, AssetId, "background", "Studio wall", "wall.png", "image/png",
        DateTimeOffset.UtcNow);

    public ImageStudioManualEditorTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new ImageProviderReadiness("foundry", true, null)]));
        Http.OnGet($"api/v1/brands/{BrandId}/assets", new List<BrandAssetResponse> { _asset });
        Http.OnPost("api/v1/blob/assets/thumbs",
            new List<AssetThumb> { new(AssetId, "https://private.example/wall-thumb.png", true) });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{ArtifactId}", Artifact());
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse>());
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/prompt-preview",
            new ImagePromptPreviewResponse("", "Auto", 1280, 720, 1536, 1024, 0, 0, false));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview(EmptySlot()));
    }

    [Fact]
    public async Task Create_from_scratch_opens_the_editor_sets_a_background_adds_a_layer_and_saves()
    {
        var filled = EmptySlot() with
        {
            State = "Filled",
            BaseImageUrl = "https://public.example/manual-base.webp",
            PublishedUrl = "https://public.example/manual-base.webp",
            UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        Http.OnPost($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/base",
            new OverlaySaveResult(filled, null));
        Http.OnGet($"api/v1/blob/assets/{AssetId}/read-sas", new ReadSas("https://private.example/wall-full.png"));

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__manual-tile").Count == 1,
            TimeSpan.FromSeconds(5));

        await view.Find(".cm-studio__manual-tile").ClickAsync();
        await view.WaitForAssertionAsync(() =>
        {
            Assert.NotNull(view.Find("[role=dialog][aria-label='Build an image']"));
            Assert.Contains("Start with a background", view.Markup, StringComparison.Ordinal);
            // The kit arrives as draggable tiles, never as click-to-add buttons.
            Assert.NotNull(view.Find("[data-bench-tile][data-tile-key='" + BrandAssetId + "']"));
        });

        // The refresh after the background lands returns the filled slot.
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview(filled));
        var bench = view.FindComponent<ImageBench>();
        await bench.Instance.DropTileOnBackground(BrandAssetId.ToString());
        await view.WaitForAssertionAsync(() =>
            Assert.Contains("manual-base.webp", view.Find("img.cm-bench__bg").GetAttribute("src"), StringComparison.Ordinal));

        await bench.Instance.DropTile("image", BrandAssetId.ToString(), 0.5, 0.5, 1.6);
        await view.WaitForAssertionAsync(() =>
            Assert.Equal("https://private.example/wall-full.png", view.Find("img[data-layer-img]").GetAttribute("src")));

        var saved = filled with
        {
            Overlay = new OverlaySpec([new OverlayBox("saved-layer", "", 0.3, 0.3, 0.4, 0.4, LogoAssetId: BrandAssetId, Kind: "image")]),
        };
        Http.OnPut($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay", new OverlaySaveResult(saved, false));
        await view.FindAll(".cm-bench__head button").Single(b => b.TextContent.Trim() == "Save image").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var request = Http.Bodies.Single(body =>
                body.Method == HttpMethod.Put && body.Path.EndsWith("/overlay", StringComparison.Ordinal));
            Assert.Contains(BrandAssetId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"kind\":\"image\"", request.Body, StringComparison.Ordinal);
            Assert.Contains("Saved and composited", view.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Escape_closes_the_dialog_and_youtube_also_has_create_from_scratch()
    {
        var youtube = Artifact() with { Kind = "youtube", Title = "Launch video" };
        var youtubeSlot = EmptySlot() with { Kind = "youtube-thumbnail" };
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [youtube], [youtubeSlot], 0, 1,
                new BrandSummaryResponse(BrandId, "Acme")));

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__manual-tile").Count == 1,
            TimeSpan.FromSeconds(5));
        Assert.Empty(view.FindAll(".cm-studio__card--add"));

        await view.Find(".cm-studio__manual-tile").ClickAsync();
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-bench")));
        await view.FindComponent<ImageBench>().Instance.Command("escape");

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Empty(view.FindAll(".cm-bench"));
            Assert.Empty(view.FindAll(".cm-studio__drawer"));
        });
    }

    /// <summary>
    /// ADR-083: a slot built in the editor has a background take underneath its layers. The
    /// gallery and the take dialog must show the finished composite (the hi-res master), not
    /// that bare background re-dressed with approximate CSS layers from thumbnails, and editing
    /// goes back to the image editor on THIS slot.
    /// </summary>
    [Fact]
    public async Task A_layered_slot_shows_its_composite_in_the_gallery_and_take_dialog_and_edits_in_the_editor()
    {
        const string background = "https://public.example/campaigns/c/images/social-card/variants/0-bg.webp";
        const string composite = "https://public.example/campaigns/c/images/social-card/composited/abc.webp";
        const string master = "https://public.example/campaigns/c/images/social-card/composited/abc@2x.webp";
        var layered = EmptySlot() with
        {
            State = "Filled",
            BaseImageUrl = background,
            PublishedUrl = composite,
            PublishedHiResUrl = master,
            Overlay = new OverlaySpec([new OverlayBox("h", "SHIP IT", 0.1, 0.1, 0.5, 0.2, Kind: "text")]),
        };
        var backgroundTake = new ImageVariantResponse(Guid.NewGuid(), SlotId, background,
            "https://public.example/campaigns/c/images/social-card/variants/thumbs/bg.webp", "manual", "Kept",
            null, null, 1280, 720, DateTimeOffset.UtcNow);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview(layered));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants", new List<ImageVariantResponse> { backgroundTake });

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card:not(.cm-studio__card--add)").Count > 0, TimeSpan.FromSeconds(5));
        await view.Find(".cm-studio__card:not(.cm-studio__card--add)").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.StartsWith(master, view.Find(".cm-studio__placed img").GetAttribute("src"), StringComparison.Ordinal);
            Assert.StartsWith(composite, view.Find(".cm-gallery button img").GetAttribute("src"), StringComparison.Ordinal);
        });

        await view.Find(".cm-gallery button").ClickAsync();
        await view.WaitForAssertionAsync(() =>
        {
            var shown = view.Find(".cm-lightbox__image");
            Assert.Equal("true", shown.GetAttribute("data-composite"));
            Assert.StartsWith(master, shown.GetAttribute("src"), StringComparison.Ordinal);
            // No legacy CSS layers drawn over it.
            Assert.Empty(view.FindAll(".cm-imgeditor__box"));
            Assert.Empty(view.FindAll("button[title^='Text overlay']"));
        });

        await view.Find("button[title^='Edit layers']").ClickAsync();
        await view.WaitForAssertionAsync(() =>
        {
            Assert.Empty(view.FindAll(".cm-lightbox"));
            Assert.NotNull(view.Find(".cm-bench"));
            Assert.Equal("text", view.Find(".cm-bench [data-layer-id=h]").GetAttribute("data-kind"));
        });
    }

    private static CampaignPreview Preview(ImageSlotResponse slot) =>
        new(Campaign(), [Artifact()], [slot], slot.State == "Filled" ? 1 : 0, 1,
            new BrandSummaryResponse(BrandId, "Acme"));

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Manual images", null,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, BrandId);

    private static ArtifactPreviewResponse Artifact() =>
        new(ArtifactId, CampaignId, "social-x", "Launch post", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static ImageSlotResponse EmptySlot() =>
        new(SlotId, CampaignId, "social-card", 1280, 720, null, null, null, null, true,
            "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: ArtifactId);
}
