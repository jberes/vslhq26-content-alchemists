using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The contact sheet (the F5-F7 studio iteration): the canvas IS the image plan. Tiles render
/// at each slot's true aspect ratio with the shared status encoding (ADR-F12), the editor is
/// a drawer that opens beside the sheet and closes back to it, and "add image" is a ghost
/// tile at the end of the content piece's own row.
/// </summary>
public sealed class ImageStudioSheetTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("91111111-1111-1111-1111-111111111111");
    private static readonly Guid EmptySlotId = Guid.Parse("91111111-1111-1111-1111-222222222222");
    private static readonly Guid FilledSlotId = Guid.Parse("91111111-1111-1111-1111-333333333333");
    private static readonly Guid BlogId = Guid.Parse("91111111-1111-1111-1111-444444444444");
    private static readonly Guid NewSlotId = Guid.Parse("91111111-1111-1111-1111-555555555555");

    public ImageStudioSheetTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new Castmill.Core.Ai.AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new Castmill.Core.Ai.ImageProviderReadiness("foundry", true, null)]));

        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [Blog()], [EmptySlot(), FilledSlot()], 1, 2));

        foreach (var slotId in new[] { EmptySlotId, FilledSlotId, NewSlotId })
        {
            Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{slotId}/variants",
                new List<ImageVariantResponse>());
        }

        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}",
            new ArtifactResponse(BlogId, CampaignId, "blog", "Enterprise grid performance",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    content = new { markdown = "# Enterprise grid performance\n\nBody copy." },
                }),
                ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_drawer_stays_closed_until_a_tile_is_chosen_and_the_close_button_returns_to_the_sheet()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card").Count > 0, TimeSpan.FromSeconds(5));

        // The whole plan, no editor: that is the point of the sheet.
        Assert.Empty(view.FindAll(".cm-studio__drawer"));

        await view.Find(".cm-studio__card:not(.cm-studio__card--add)").ClickAsync();
        Assert.NotEmpty(view.FindAll(".cm-studio__drawer"));

        await view.Find(".cm-studio__drawer-close").ClickAsync();
        Assert.Empty(view.FindAll(".cm-studio__drawer"));
        Assert.NotEmpty(view.FindAll(".cm-studio__card")); // back on the sheet, nothing lost
    }

    [Fact]
    public async Task Escape_inside_the_drawer_closes_it()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card").Count > 0, TimeSpan.FromSeconds(5));
        await view.Find(".cm-studio__card:not(.cm-studio__card--add)").ClickAsync();

        var drawer = view.Find(".cm-studio__drawer");
        await drawer.KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(view.FindAll(".cm-studio__drawer"));
    }

    [Fact]
    public async Task Tiles_carry_the_slots_true_aspect_ratio_and_visible_state()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card:not(.cm-studio__card--add)").Count == 2,
            TimeSpan.FromSeconds(5));

        var tiles = view.FindAll(".cm-studio__card:not(.cm-studio__card--add)");

        var empty = tiles.Single(t => t.TextContent.Contains("Empty", StringComparison.Ordinal));
        Assert.Contains("aspect-ratio: 1600 / 840", empty.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Null(empty.QuerySelector("img")); // an empty slot is a visible hole, not a picture

        var filled = tiles.Single(t => t.TextContent.Contains("Done", StringComparison.Ordinal));
        Assert.Contains("aspect-ratio: 1280 / 720", filled.GetAttribute("style"), StringComparison.Ordinal);
        var img = filled.QuerySelector("img");
        Assert.NotNull(img);
        Assert.Contains("published.example", img!.GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slot_with_unplaced_takes_previews_its_best_take_instead_of_an_empty_hole()
    {
        var withTakes = EmptySlot() with { LatestTakeThumbUrl = "https://public.example/thumbs/take-7.webp" };
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [Blog()], [withTakes], 0, 1));

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card:not(.cm-studio__card--add)").Count == 1,
            TimeSpan.FromSeconds(5));

        var tile = view.Find(".cm-studio__card:not(.cm-studio__card--add)");
        Assert.Contains("In takes", tile.TextContent, StringComparison.Ordinal);
        Assert.Contains("thumbs/take-7", tile.QuerySelector("img")!.GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_group_header_counts_filled_slots_against_the_plan()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__fill").Count > 0, TimeSpan.FromSeconds(5));

        // One blog group holding both slots: one filled of two.
        Assert.Equal("1/2", view.Find(".cm-studio__fill").TextContent);
    }

    [Fact]
    public async Task The_add_tile_creates_a_slot_in_that_content_piece_and_opens_its_drawer()
    {
        Http.OnPost($"api/v1/campaigns/{CampaignId}/image-slots", NewSlot());

        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__card--add").Count == 1, TimeSpan.FromSeconds(5));

        // The forced reload after creation must return the new slot as part of the plan,
        // or the sheet would rightly close the drawer for a slot that "doesn't exist".
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [Blog()], [EmptySlot(), FilledSlot(), NewSlot()], 1, 3));

        await view.Find(".cm-studio__card--add").ClickAsync();

        await view.WaitForStateAsync(
            () => view.FindAll(".cm-studio__drawer").Count == 1, TimeSpan.FromSeconds(5));
        var (_, _, body) = Http.Bodies.Last(b =>
            b.Method == HttpMethod.Post && b.Path.EndsWith("/image-slots", StringComparison.Ordinal));
        Assert.Contains(BlogId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ---------------------------------------------------------------

    private static ImageSlotResponse EmptySlot() => new(
        EmptySlotId, CampaignId, "blog-header", 1600, 840,
        "a hero image", "gpt-image-2", null, null, true,
        "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: BlogId);

    private static ImageSlotResponse FilledSlot() => new(
        FilledSlotId, CampaignId, "blog-inline-1", 1280, 720,
        "an inline image", "gpt-image-2", null, null, true,
        "Filled", "https://published.example/x.webp", "https://published.example/x.webp",
        DateTimeOffset.UtcNow, ArtifactId: BlogId);

    private static ImageSlotResponse NewSlot() => new(
        NewSlotId, CampaignId, "blog-inline-2", 1200, 675,
        null, null, null, null, true,
        "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: BlogId);

    private static ArtifactPreviewResponse Blog() =>
        new(BlogId, CampaignId, "blog", "Enterprise grid performance", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
