using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The take lightbox. The old dialog capped the image at the width of a text dialog, which
/// made judging a 1600×840 header impossible — the one thing this screen exists for — and put
/// the overlay controls on a different surface from the image they change.
/// </summary>
public sealed class ImageStudioLightboxTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("f1111111-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("f1111111-1111-1111-1111-222222222222");
    private static readonly Guid TakeId = Guid.Parse("f1111111-1111-1111-1111-333333333333");

    public ImageStudioLightboxTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new Castmill.Core.Ai.AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new Castmill.Core.Ai.ImageProviderReadiness("foundry", true, null)]));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [], [Slot()], 0, 6));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse> { Take() });
    }

    [Fact]
    public async Task The_take_is_shown_at_full_size_on_its_own_stage()
    {
        var view = await OpenAsync();

        var image = view.Find(".cm-lightbox__image");
        // The FULL-size url, never the gallery thumbnail: the point is judging the real image.
        Assert.Equal(Take().Url, image.GetAttribute("src"));
        Assert.NotNull(view.Find(".cm-lightbox__stage"));
    }

    /// <summary>
    /// Overlay controls belong beside the image they change. A generated background is busy
    /// and unpredictable, so a shadow alone does not always keep a headline legible — the
    /// band is the reliable answer, and it is the author's call.
    /// </summary>
    [Fact]
    public async Task The_overlay_band_colour_travels_with_the_composite_request()
    {
        var view = await OpenAsync();

        view.Find(".cm-lightbox__rail input.cm-input").Change("Deploy time, halved");

        var swatches = view.FindAll(".cm-ring");
        Assert.True(swatches.Count >= 2, "expected a choice of band colours");
        await swatches[1].ClickAsync(); // the first non-null colour

        Http.OnPost("api/v1/images/composite",
            new PlaceResult(Slot("Filled"), null, false));

        await view.FindAll(".cm-lightbox__rail button")
            .First(b => b.TextContent.Contains("Apply overlay", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForStateAsync(
            () => Http.Bodies.Any(b => b.Path.EndsWith("images/composite", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        var body = Http.Bodies.Last(b => b.Path.EndsWith("images/composite", StringComparison.Ordinal)).Body;
        Assert.Contains("Deploy time, halved", body, StringComparison.Ordinal);
        Assert.Contains("headlineBackground", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_no_band_sends_no_colour()
    {
        var view = await OpenAsync();
        view.Find(".cm-lightbox__rail input.cm-input").Change("Headline");

        await view.FindAll(".cm-ring")[0].ClickAsync(); // "No band"

        Http.OnPost("api/v1/images/composite", new PlaceResult(Slot("Filled"), null, false));
        await view.FindAll(".cm-lightbox__rail button")
            .First(b => b.TextContent.Contains("Apply overlay", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForStateAsync(
            () => Http.Bodies.Any(b => b.Path.EndsWith("images/composite", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        var body = Http.Bodies.Last(b => b.Path.EndsWith("images/composite", StringComparison.Ordinal)).Body;
        Assert.Contains("\"headlineBackground\":null", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<ImageStudioView>> OpenAsync()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));
        await view.Find(".cm-gallery__tile").ClickAsync();
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-lightbox").Count == 1, TimeSpan.FromSeconds(5));
        return view;
    }

    private static ImageSlotResponse Slot(string state = "Empty") => new(
        SlotId, CampaignId, "youtube-thumbnail", 1280, 720, "a bold thumbnail", "gpt-image-2",
        null, null, SafeArea: true, state,
        state == "Filled" ? "https://public.example/x.webp" : null,
        state == "Filled" ? "https://public.example/x.webp" : null,
        DateTimeOffset.UtcNow);

    private static ImageVariantResponse Take() => new(
        TakeId, SlotId,
        "https://public.example/campaigns/x/images/youtube-thumbnail/variants/1-full.webp",
        "https://public.example/campaigns/x/images/youtube-thumbnail/variants/thumbs/1.webp",
        "gpt-image-2", "Candidate", null, null, 1280, 720, DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
