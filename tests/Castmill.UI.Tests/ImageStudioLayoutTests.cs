using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The studio layout (ADR-F77): a navigator that always lists every content item, a stage where
/// the selected take is the hero with its takes in a filmstrip, an inspector in tabs with one
/// generate dock, a board for the whole campaign, and Extract from video as a side sheet.
/// </summary>
public sealed class ImageStudioLayoutTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("a7777777-1111-1111-1111-111111111111");
    private static readonly Guid BlogId = Guid.Parse("a7777777-1111-1111-1111-222222222222");
    private static readonly Guid XPostId = Guid.Parse("a7777777-1111-1111-1111-333333333333");
    private static readonly Guid HeaderSlotId = Guid.Parse("a7777777-1111-1111-1111-444444444444");
    private static readonly Guid CardSlotId = Guid.Parse("a7777777-1111-1111-1111-555555555555");
    private static readonly Guid PlacedTakeId = Guid.Parse("a7777777-1111-1111-1111-666666666666");
    private static readonly Guid NewerTakeId = Guid.Parse("a7777777-1111-1111-1111-777777777777");
    private const string PlacedUrl = "https://public.example/c/images/blog-header/variants/placed.webp";
    private readonly RecordingConfirm _confirm = new();

    public ImageStudioLayoutTests()
    {
        SignInTestUser();
        Services.AddScoped<IConfirmService>(_ => _confirm);
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new Castmill.Core.Ai.AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new Castmill.Core.Ai.ImageProviderReadiness("foundry", true, null)]));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [Blog(), XPost()], [HeaderSlot(), CardSlot()], 1, 2));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{HeaderSlotId}/variants",
            new List<ImageVariantResponse> { Take(NewerTakeId, "https://public.example/c/newer.webp", minutesAgo: 1), Take(PlacedTakeId, PlacedUrl, minutesAgo: 30) });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{CardSlotId}/variants", new List<ImageVariantResponse>());
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{HeaderSlotId}/prompt-preview",
            new ImagePromptPreviewResponse("A split-screen scene.", "Manual", 1600, 840, 2752, 1536, 0, 0, false));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{CardSlotId}/prompt-preview",
            new ImagePromptPreviewResponse("A poster.", "Manual", 1200, 1200, 2048, 2048, 0, 0, false));
    }

    [Fact]
    public async Task With_nothing_selected_the_board_shows_every_item_and_choosing_a_tile_opens_the_stage()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-studio__board .cm-studio__tile").Count == 2, TimeSpan.FromSeconds(5));

        Assert.Empty(view.FindAll(".cm-studio__drawer"));
        Assert.True(view.FindAll(".cm-studio__view-btn").Single(b => b.TextContent == "Stage").HasAttribute("disabled"));

        await view.FindAll(".cm-studio__board .cm-studio__card")[0].ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.NotEmpty(view.FindAll(".cm-studio__stagecol"));
            Assert.NotEmpty(view.FindAll(".cm-studio__drawer"));
            Assert.Empty(view.FindAll(".cm-studio__board"));
        });
    }

    /// <summary>The reported bug: selecting a slot hid every other content item.</summary>
    [Fact]
    public async Task Selecting_a_slot_keeps_every_content_item_in_the_navigator()
    {
        var view = await OpenHeaderAsync();

        var navigator = view.Find(".cm-studio__sheet").TextContent;
        Assert.Contains("Enterprise grid performance", navigator, StringComparison.Ordinal);
        Assert.Contains("Conversational analytics", navigator, StringComparison.Ordinal);
        Assert.Equal(2, view.FindAll(".cm-studio__group").Count);
        Assert.Equal("true", view.Find(".cm-studio__card--active").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Generate_all_pending_covers_the_whole_campaign_even_with_a_slot_open()
    {
        var view = await OpenHeaderAsync();
        var gate = Http.Gate(HttpMethod.Post, $"api/v1/campaigns/{CampaignId}/image-slots/generate-pending");

        await view.FindAll(".cm-studio__batch button").Single(b => b.TextContent.Trim() == "Generate all pending").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Path.EndsWith("/generate-pending", StringComparison.Ordinal)).Body;
            Assert.Contains("\"artifactId\":null", body, StringComparison.Ordinal);
        });
        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task The_stage_shows_the_placed_take_and_the_filmstrip_marks_it()
    {
        var view = await OpenHeaderAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Equal(PlacedUrl, view.Find(".cm-lightbox--inline .cm-lightbox__image").GetAttribute("src"));
            var current = view.FindAll(".cm-studio__film .cm-gallery__tile[aria-current='true']");
            Assert.Single(current);
        });

        // Picking another take in the filmstrip puts it on the stage.
        await view.FindAll(".cm-studio__film .cm-gallery__tile")[0].ClickAsync();
        Assert.Equal("https://public.example/c/newer.webp", view.Find(".cm-lightbox--inline .cm-lightbox__image").GetAttribute("src"));
    }

    [Fact]
    public async Task The_inspector_tabs_switch_content_and_the_generate_dock_is_always_there()
    {
        var view = await OpenHeaderAsync();

        foreach (var (tab, marker) in new[] { ("prompt", "Prompt mode"), ("refine", "Steer a new take"), ("text", "Overlay text"), ("details", "Belongs to") })
        {
            await view.Find($"#cm-studio-tab-{tab}").ClickAsync();
            await view.WaitForAssertionAsync(() =>
            {
                Assert.Equal("true", view.Find($"#cm-studio-tab-{tab}").GetAttribute("aria-selected"));
                Assert.Contains(marker, view.Find(".cm-studio__panel").TextContent, StringComparison.Ordinal);
                var dock = view.Find(".cm-studio__dock").TextContent;
                Assert.Contains("Model", dock, StringComparison.Ordinal);
                Assert.Contains("Generate", dock, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task Select_region_opens_refine_and_text_overlay_opens_text()
    {
        var view = await OpenHeaderAsync();
        await view.WaitForAssertionAsync(() => Assert.NotEmpty(view.FindAll(".cm-lightbox__toolbar")));

        await Toolbar(view, "Select region").ClickAsync();
        Assert.Equal("true", view.Find("#cm-studio-tab-refine").GetAttribute("aria-selected"));
        Assert.Contains("Edit a region", view.Find(".cm-studio__panel").TextContent, StringComparison.Ordinal);

        await Toolbar(view, "Select region").ClickAsync();
        await Toolbar(view, "Text overlay").ClickAsync();
        Assert.Equal("true", view.Find("#cm-studio-tab-text").GetAttribute("aria-selected"));
        Assert.NotEmpty(view.FindAll(".cm-studio__panel .cm-imgeditor-panel"));
    }

    [Fact]
    public async Task Switching_takes_with_unsaved_overlay_edits_asks_first()
    {
        var view = await OpenHeaderAsync();
        await view.WaitForAssertionAsync(() => Assert.NotEmpty(view.FindAll(".cm-lightbox__toolbar")));
        await Toolbar(view, "Text overlay").ClickAsync();
        view.Find(".cm-imgeditor-panel textarea").Input("Ship it");

        _confirm.Answer = false;
        await view.FindAll(".cm-studio__film .cm-gallery__tile")[0].ClickAsync();

        Assert.Contains("Unsaved overlay changes", Assert.Single(_confirm.Requests).Title, StringComparison.Ordinal);
        // Declined: the edited take stays on the stage with the edit still there.
        Assert.Equal("Ship it", view.Find(".cm-imgeditor-panel textarea").GetAttribute("value"));
    }

    [Fact]
    public async Task Compare_on_the_stage_is_an_a_b_split_with_a_slider()
    {
        var view = await OpenHeaderAsync();
        await view.WaitForAssertionAsync(() => Assert.NotEmpty(view.FindAll(".cm-lightbox__toolbar")));

        await Toolbar(view, "Compare").ClickAsync();

        var slider = view.Find(".cm-lightbox--inline input.cm-compare-slider");
        Assert.Equal("50", slider.GetAttribute("value"));
        await slider.InputAsync("30");
        Assert.Contains("--cm-split: 30%", view.Find(".cm-lightbox__canvas--compare").GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal(2, view.FindAll(".cm-lightbox__canvas img").Count);
    }

    [Fact]
    public async Task An_empty_slot_offers_generate_and_create_from_scratch_on_the_stage()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-studio__card--row").Count == 2, TimeSpan.FromSeconds(5));
        await view.FindAll(".cm-studio__card--row").Single(r => r.TextContent.Contains("Social card", StringComparison.Ordinal)).ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var empty = view.Find(".cm-studio__stage-empty");
            Assert.Contains("No takes for Social card yet", empty.TextContent, StringComparison.Ordinal);
            Assert.Contains("Generate 1 variant", empty.TextContent, StringComparison.Ordinal);
            Assert.Contains("Create from scratch", empty.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Extract_from_video_opens_as_a_side_sheet_and_escape_closes_it()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-studio__batch").Count == 1, TimeSpan.FromSeconds(5));

        await view.FindAll(".cm-studio__batch button").Single(b => b.TextContent.Trim() == "Extract from video").ClickAsync();

        var sheet = view.Find("aside.cm-sheet[aria-label='Extract from video']");
        Assert.NotNull(sheet.QuerySelector(".cm-reference-page--sheet"));
        Assert.DoesNotContain("Back to Image Studio", sheet.TextContent, StringComparison.Ordinal);

        await sheet.KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(view.FindAll("aside.cm-sheet"));
    }

    private async Task<IRenderedComponent<ImageStudioView>> OpenHeaderAsync()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-studio__card--row").Count == 2, TimeSpan.FromSeconds(5));
        await view.FindAll(".cm-studio__card--row").Single(r => r.TextContent.Contains("Blog header", StringComparison.Ordinal)).ClickAsync();
        await view.WaitForStateAsync(() => view.FindAll(".cm-studio__drawer").Count == 1, TimeSpan.FromSeconds(5));
        return view;
    }

    private static AngleSharp.Dom.IElement Toolbar(IRenderedComponent<ImageStudioView> view, string label) =>
        view.FindAll(".cm-lightbox__toolbar button").Single(b => b.TextContent.Trim() == label);

    private static ImageVariantResponse Take(Guid id, string url, int minutesAgo) => new(
        id, HeaderSlotId, url, url.Replace(".webp", "-thumb.webp", StringComparison.Ordinal),
        "gpt-image-2", "Candidate", null, null, 1600, 840, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    private static ImageSlotResponse HeaderSlot() => new ImageSlotResponse(
        HeaderSlotId, CampaignId, "blog-header", 1600, 840, "a hero image", "gpt-image-2", null, null, true,
        "Filled", PlacedUrl, PlacedUrl, DateTimeOffset.UtcNow, ArtifactId: BlogId, PromptMode: "Manual")
        with { BaseImageUrl = PlacedUrl };

    private static ImageSlotResponse CardSlot() => new(
        CardSlotId, CampaignId, "social-card", 1200, 1200, "a poster", "gpt-image-2", null, null, true,
        "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: XPostId, PromptMode: "Manual");

    private static ArtifactPreviewResponse Blog() =>
        new(BlogId, CampaignId, "blog", "Enterprise grid performance", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse XPost() =>
        new(XPostId, CampaignId, "social-x", "Conversational analytics", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Studio layout campaign", null, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private sealed class RecordingConfirm : IConfirmService
    {
        public bool Answer { get; set; } = true;

        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(Answer);
        }
    }
}
