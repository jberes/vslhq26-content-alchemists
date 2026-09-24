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
    public async Task Kit_background_then_image_layer_renders_and_saves_the_layer_asset_id()
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
