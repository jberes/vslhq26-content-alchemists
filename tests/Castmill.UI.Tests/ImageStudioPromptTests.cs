using Bunit;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// Prompt transparency and model comparison (ADR-054). Auto mode used to disable the
/// textarea and show nothing — a bad render could not be diagnosed from the studio — and
/// comparing two models meant two separate runs with the model changed in between.
/// </summary>
public sealed class ImageStudioPromptTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("a2222222-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("a2222222-1111-1111-1111-222222222222");
    private static readonly Guid TakeId = Guid.Parse("a2222222-1111-1111-1111-333333333333");

    private const string PreviewText = "Create a YouTube thumbnail for the content item \"Launch\".\nCOMPOSITION REQUIREMENTS";
    private const string TakePrompt = "a bold thumbnail\nAdjustment: warmer background";

    public ImageStudioPromptTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [
                new ImageProviderReadiness("foundry", true, null),
                new ImageProviderReadiness("nano-banana", true, null),
            ]));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [], [Slot()], 0, 6));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse> { Take() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/prompt-preview",
            new ImagePromptPreviewResponse(PreviewText, "Auto", 1280, 720, 1536, 1024, 0, 7.8, false));
        Http.OnPost($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/generate",
            new VariantBatchResponse(Guid.NewGuid(), SlotId, "youtube-thumbnail", [], []));
    }

    [Fact]
    public async Task The_drawer_shows_the_exact_prompt_the_next_generate_will_send()
    {
        var view = await OpenSlotAsync();

        await view.WaitForAssertionAsync(() =>
            Assert.Contains(PreviewText, view.Find(".cm-studio__drawer .cm-studio__preview-text").TextContent,
                StringComparison.Ordinal));
        Assert.Contains("Prompt Castmill will send", view.Find(".cm-studio__drawer .cm-studio__preview summary").TextContent,
            StringComparison.Ordinal);
        // The size line tells the truth about the crop instead of claiming an exact render.
        var head = view.Find(".cm-studio__drawer-head").TextContent;
        Assert.Contains("Published at 1280×720", head, StringComparison.Ordinal);
        Assert.Contains("1536×1024", head, StringComparison.Ordinal);
        Assert.DoesNotContain("no client-side cropping", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_lightbox_shows_the_prompt_a_take_was_rendered_from_and_can_regenerate_it()
    {
        var view = await OpenTakeAsync();

        Assert.Contains(TakePrompt, view.Find(".cm-lightbox__rail .cm-studio__preview-text").TextContent,
            StringComparison.Ordinal);

        await view.FindAll(".cm-lightbox__toolbar button")
            .Single(button => button.TextContent.Trim() == "Regenerate").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Method == HttpMethod.Post
                && b.Path.EndsWith("/generate", StringComparison.Ordinal)).Body;
            // The take's own model, one fresh take.
            Assert.Contains("\"modelAlias\":\"nano-banana\"", body, StringComparison.Ordinal);
            Assert.Contains("\"variants\":1", body, StringComparison.Ordinal);
        });
        Assert.Empty(view.FindAll(".cm-lightbox"));
    }

    [Fact]
    public async Task Compare_renders_the_same_prompt_once_on_every_ready_model_in_one_run()
    {
        var view = await OpenSlotAsync();

        var compare = view.FindAll(".cm-studio__compare button").Single();
        Assert.Contains("Compare 2 models", compare.TextContent, StringComparison.Ordinal);
        await compare.ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Method == HttpMethod.Post
                && b.Path.EndsWith("/generate", StringComparison.Ordinal)).Body;
            Assert.Contains("\"modelAliases\":[\"foundry\",\"nano-banana\"]", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_art_director_pass_is_off_by_default_and_rides_on_the_generate_request()
    {
        Http.OnPatch($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}", Slot());
        var view = await OpenSlotAsync();

        var toggle = view.Find(".cm-studio__critic input[type=checkbox]");
        Assert.False(toggle.HasAttribute("checked"));
        Assert.Contains("Off:", view.Find(".cm-studio__critic .cm-meta").TextContent, StringComparison.Ordinal);

        await toggle.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        Assert.Contains("re-rendered on a defect", view.Find(".cm-studio__critic .cm-meta").TextContent, StringComparison.Ordinal);

        await view.FindAll(".cm-studio__row > button.cm-button")
            .Single(button => button.TextContent.Contains("Generate 1 variant", StringComparison.Ordinal)).ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Method == HttpMethod.Post
                && b.Path.EndsWith("/generate", StringComparison.Ordinal)).Body;
            Assert.Contains("\"critique\":true", body, StringComparison.Ordinal);
            Assert.Contains("\"variants\":1", body, StringComparison.Ordinal);
        });
    }

    private async Task<IRenderedComponent<ImageStudioView>> OpenSlotAsync()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card:not(.cm-studio__card--add)").Count >= 1, TimeSpan.FromSeconds(5));
        await view.Find(".cm-studio__card:not(.cm-studio__card--add)").ClickAsync();
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__drawer").Count == 1, TimeSpan.FromSeconds(5));
        return view;
    }

    private async Task<IRenderedComponent<ImageStudioView>> OpenTakeAsync()
    {
        var view = await OpenSlotAsync();
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));
        await view.Find(".cm-gallery__tile").ClickAsync();
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-lightbox").Count == 1, TimeSpan.FromSeconds(5));
        return view;
    }

    private static ImageSlotResponse Slot() => new(
        SlotId, CampaignId, "youtube-thumbnail", 1280, 720, "a bold thumbnail", null,
        null, null, SafeArea: true, "Empty", null, null, DateTimeOffset.UtcNow);

    private static ImageVariantResponse Take() => new(
        TakeId, SlotId,
        "https://public.example/campaigns/x/images/youtube-thumbnail/variants/1-full.webp",
        "https://public.example/campaigns/x/images/youtube-thumbnail/variants/thumbs/1.webp",
        "nano-banana", "Candidate", "warmer background", null, 1280, 720, DateTimeOffset.UtcNow,
        Prompt: TakePrompt);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Launch", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
