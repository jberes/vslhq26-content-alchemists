using Bunit;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The lightbox as an editor (ADR-055): overlay boxes, a region mask, A/B compare and the
/// take's history. The JS island is absent under bUnit, so these pin the .NET half — what
/// renders, what is enabled, what is sent.
/// </summary>
public sealed class ImageStudioEditorTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("b3333333-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("b3333333-1111-1111-1111-222222222222");
    private static readonly Guid TakeA = Guid.Parse("b3333333-1111-1111-1111-333333333333");
    private static readonly Guid TakeB = Guid.Parse("b3333333-1111-1111-1111-444444444444");

    private readonly RecordingConfirm _confirm = new();

    public ImageStudioEditorTests()
    {
        Services.AddSingleton<IConfirmService>(_confirm);
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new ImageProviderReadiness("foundry", true, null)]));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(Campaign(), [], [Slot()], 0, 6));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse> { Take(TakeB, source: TakeA, note: "warmer light"), Take(TakeA) });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/prompt-preview",
            new ImagePromptPreviewResponse("p", "Auto", 1280, 720, 1536, 1024, 0, 7.8, false));
    }

    [Fact]
    public async Task Text_overlay_opens_the_editor_with_a_box_and_save_sends_the_spec()
    {
        var view = await OpenTakeAsync(TakeA);

        await ToolbarButton(view, "Text overlay").ClickAsync();
        Assert.NotNull(view.Find(".cm-imgeditor-panel"));
        Assert.Single(view.FindAll(".cm-imgeditor__box"));

        view.Find(".cm-imgeditor-panel textarea").Input("Deploy time, halved");
        Assert.Contains("Deploy time, halved", view.Find(".cm-imgeditor__box").TextContent, StringComparison.Ordinal);

        Http.OnPut($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay",
            new OverlaySaveResult(Slot() with { State = "Filled" }, false));
        await view.FindAll(".cm-imgeditor-panel button").Single(b => b.TextContent.Trim() == "Save overlay").ClickAsync();
        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Method == HttpMethod.Put && b.Path.EndsWith("/overlay", StringComparison.Ordinal)).Body;
            Assert.Contains("\"boxes\":[", body, StringComparison.Ordinal);
            Assert.Contains("Deploy time, halved", body, StringComparison.Ordinal);
        });
        Assert.Contains("composited onto the placed image", view.Markup, StringComparison.Ordinal);
    }

    /// <summary>The toolbar Save: disabled until an edit exists, saves, then reads Saved; closing with
    /// unsaved edits asks first.</summary>
    [Fact]
    public async Task Toolbar_save_enables_on_edits_persists_and_guards_close()
    {
        var view = await OpenTakeAsync(TakeA);
        var save = () => view.Find(".cm-lightbox__save");
        Assert.True(save().HasAttribute("disabled"));

        await ToolbarButton(view, "Text overlay").ClickAsync();
        view.Find(".cm-imgeditor-panel textarea").Input("Ship it");
        Assert.False(save().HasAttribute("disabled"));
        Assert.Equal("Save", save().TextContent.Trim());

        // Closing with unsaved edits asks; declining keeps the lightbox open.
        _confirm.Answer = false;
        await view.Find("button.cm-lightbox__close").ClickAsync();
        Assert.NotEmpty(view.FindAll(".cm-lightbox"));
        Assert.Contains("Unsaved overlay changes", Assert.Single(_confirm.Requests).Title, StringComparison.Ordinal);

        Http.OnPut($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/overlay", new OverlaySaveResult(Slot(), null));
        await save().ClickAsync();
        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Ship it", Http.Bodies.Single(b => b.Method == HttpMethod.Put && b.Path.EndsWith("/overlay", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
            Assert.Equal("Saved", save().TextContent.Trim());
        });
        Assert.True(save().HasAttribute("disabled"));

        // Clean now: close needs no confirmation.
        _confirm.Requests.Clear();
        await view.Find("button.cm-lightbox__close").ClickAsync();
        await view.WaitForAssertionAsync(() => Assert.Empty(view.FindAll(".cm-lightbox")));
        Assert.Empty(_confirm.Requests);
    }

    [Fact]
    public async Task Select_region_shows_the_repaint_panel_and_waits_for_a_mask()
    {
        var view = await OpenTakeAsync(TakeA);

        await ToolbarButton(view, "Select region").ClickAsync();
        var panel = view.Find(".cm-imgeditor-panel");
        Assert.Contains("Drag a rectangle", panel.TextContent, StringComparison.Ordinal);
        var repaint = panel.QuerySelectorAll("button").Single(b => b.TextContent.Contains("Repaint region", StringComparison.Ordinal));
        Assert.True(repaint.HasAttribute("disabled"), "no mask drawn yet");

        // The JS island reports a drawn rectangle through the same JSInvokable the browser uses.
        await view.InvokeAsync(() => view.Instance.MaskDrawn(0.1, 0.2, 0.5, 0.4));
        Assert.Contains("Region selected: 50% × 40%", view.Find(".cm-imgeditor-panel").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_puts_a_second_take_beside_the_first()
    {
        var view = await OpenTakeAsync(TakeA);

        await ToolbarButton(view, "Compare").ClickAsync();
        Assert.NotNull(view.Find(".cm-lightbox__canvas--compare"));
        Assert.Equal(2, view.FindAll(".cm-lightbox__canvas img").Count);
        Assert.Contains("B · gpt-image-2", view.Find(".cm-imgeditor__label").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Take_history_shows_the_chain_a_take_was_steered_from()
    {
        var view = await OpenTakeAsync(TakeB);

        var nodes = view.FindAll(".cm-lineage__node");
        Assert.Equal(2, nodes.Count);
        Assert.Contains("origin", nodes[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("cm-lineage__node--current", nodes[1].ClassName, StringComparison.Ordinal);
    }

    private static AngleSharp.Dom.IElement ToolbarButton(IRenderedComponent<ImageStudioView> view, string label) =>
        view.FindAll(".cm-lightbox__toolbar button").Single(b => b.TextContent.Trim() == label);

    private async Task<IRenderedComponent<ImageStudioView>> OpenTakeAsync(Guid takeId)
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-studio__card:not(.cm-studio__card--add)").Count >= 1, TimeSpan.FromSeconds(5));
        await view.Find(".cm-studio__card:not(.cm-studio__card--add)").ClickAsync();
        await view.WaitForStateAsync(() => view.FindAll(".cm-gallery__tile").Count == 2, TimeSpan.FromSeconds(5));
        var tiles = view.FindAll(".cm-gallery__tile");
        var index = takeId == TakeB ? 0 : 1; // newest first
        await tiles[index].ClickAsync();
        await view.WaitForStateAsync(() => view.FindAll(".cm-lightbox").Count == 1, TimeSpan.FromSeconds(5));
        return view;
    }

    private static ImageSlotResponse Slot() => new(
        SlotId, CampaignId, "youtube-thumbnail", 1280, 720, "a bold thumbnail", null,
        null, null, SafeArea: true, "Empty", null, null, DateTimeOffset.UtcNow);

    private static ImageVariantResponse Take(Guid id, Guid? source = null, string? note = null) => new(
        id, SlotId, $"https://public.example/{id}.webp", $"https://public.example/{id}-thumb.webp",
        "gpt-image-2", "Candidate", note, source, 1280, 720,
        source is null ? DateTimeOffset.UtcNow.AddMinutes(-10) : DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Launch", null, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

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
