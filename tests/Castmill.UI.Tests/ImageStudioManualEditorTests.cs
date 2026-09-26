using Bunit;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The create-from-scratch dialog is a complete workflow, not another route into the generated
/// take lightbox. These checks exercise the clicks a producer makes: open, choose a background,
/// add an image layer, save the composite, and close with Escape.
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
    public async Task Image_layer_can_be_cropped_shaped_saved_and_deleted()
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

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__manual-tile").Count == 1,
            TimeSpan.FromSeconds(5));

        await view.Find(".cm-studio__manual-tile").ClickAsync();
        await view.WaitForAssertionAsync(() =>
        {
            Assert.NotNull(view.Find(".cm-manual"));
            Assert.Contains("Start with a background", view.Markup, StringComparison.Ordinal);
        });

        // Refresh after applying the background returns the filled slot.
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview(filled));
        await view.Find(".cm-manual__rail .cm-studio__pick").ClickAsync();
        await view.WaitForAssertionAsync(() =>
        {
            var canvas = view.Find(".cm-imgeditor");
            Assert.Contains("--ar:1.7778", canvas.GetAttribute("style"), StringComparison.Ordinal);
        });

        await view.FindAll(".cm-manual__rail button")
            .Single(button => button.TextContent.Trim() == "+ Image")
            .ClickAsync();
        await view.Find(".cm-layer-picker .cm-studio__pick").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var layer = view.Find(".cm-imgeditor__box .cm-imgeditor__layer");
            Assert.Contains("wall-thumb.png", layer.GetAttribute("src"), StringComparison.Ordinal);
        });

        view.Find("select[aria-label='Layer shape']").Change("circle");
        view.Find("input[aria-label='Crop zoom']").Input("2.25");
        view.Find("input[aria-label='Crop horizontal focus']").Input("0.8");
        view.Find("input[aria-label='Crop vertical focus']").Input("0.3");
        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains("border-radius:50%", view.Find(".cm-imgeditor__box").GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("cm-imgeditor__layer--cropped", view.Find(".cm-imgeditor__layer").ClassList);
        });

        var saved = filled with
        {
            Overlay = new OverlaySpec([
                new OverlayBox("saved-layer", "", 0.08, 0.08, 0.34, 0.34,
                    LogoAssetId: BrandAssetId),
            ]),
        };
        Http.OnPut($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay",
            new OverlaySaveResult(saved, false));
        await view.Find(".cm-manual__save button").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var request = Http.Bodies.Single(body =>
                body.Method == HttpMethod.Put && body.Path.EndsWith("/overlay", StringComparison.Ordinal));
            Assert.Contains(BrandAssetId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"shape\":\"circle\"", request.Body, StringComparison.Ordinal);
            Assert.Contains("\"zoom\":2.25", request.Body, StringComparison.Ordinal);
            Assert.Contains("\"focusX\":0.8", request.Body, StringComparison.Ordinal);
            Assert.Contains("\"focusY\":0.3", request.Body, StringComparison.Ordinal);
            Assert.Contains("Saved and composited", view.Markup, StringComparison.Ordinal);
        });

        Http.OnAsync(HttpMethod.Delete,
            $"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay",
            () => Task.FromResult(StubHttpHandler.Json(filled)));
        await view.Find("button[aria-label='Delete selected layer']").ClickAsync();
        Assert.Empty(view.FindAll(".cm-imgeditor__layer"));
        await view.Find(".cm-manual__save button").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains(Http.Requests, request => request.Method == HttpMethod.Delete
                && request.RequestUri!.AbsolutePath.EndsWith("/overlay", StringComparison.Ordinal));
            Assert.Contains("All layers deleted", view.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Text_layer_saves_background_colour_and_opacity()
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

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__manual-tile").Count == 1,
            TimeSpan.FromSeconds(5));
        await view.Find(".cm-studio__manual-tile").ClickAsync();
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview(filled));
        await view.Find(".cm-manual__rail .cm-studio__pick").ClickAsync();

        await view.FindAll(".cm-manual__rail button")
            .Single(button => button.TextContent.Trim() == "+ Text")
            .ClickAsync();
        view.Find("input[aria-label='Text background']").Change(true);
        view.Find("input[aria-label='Background colour']").Change("#336699");
        view.Find("input[aria-label='Background opacity']").Input("0.35");

        Http.OnPut($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay",
            new OverlaySaveResult(filled, false));
        await view.Find(".cm-manual__save button").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var request = Http.Bodies.Single(body =>
                body.Method == HttpMethod.Put && body.Path.EndsWith("/overlay", StringComparison.Ordinal));
            Assert.Contains("\"color\":\"#336699\"", request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"opacity\":0.35", request.Body, StringComparison.Ordinal);
            Assert.Contains("--cm-imgeditor-band:#33669959", view.Find(".cm-imgeditor__box").GetAttribute("style"), StringComparison.OrdinalIgnoreCase);
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
        await view.InvokeAsync(view.Instance.ManualEscapeAsync);

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Empty(view.FindAll(".cm-manual"));
            Assert.Empty(view.FindAll(".cm-studio__drawer"));
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
