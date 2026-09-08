using System.Net;
using Bunit;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// ADR-F68/F69: in Auto mode the studio shows the written brief where the prompt would be —
/// a disabled empty textarea read as "there was no prompt" — with a way to rewrite it, and
/// the drawer links straight back to the content item the image belongs to.
/// </summary>
public sealed class ImageStudioBriefTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("b3333333-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("b3333333-1111-1111-1111-222222222222");
    private static readonly Guid ArtifactId = Guid.Parse("b3333333-1111-1111-1111-444444444444");
    private const string Brief = "Dark navy studio backdrop, one simplified data grid floating centre-right, cyan glow.\n\nCOMPOSITION REQUIREMENTS";

    public ImageStudioBriefTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new ImageProviderReadiness("foundry", true, null)]));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants", new List<ImageVariantResponse>());
        Http.OnStatus(HttpMethod.Post, $"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/brief/rewrite", HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Auto_mode_shows_the_written_brief_in_place_of_the_prompt_and_can_rewrite_it()
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(Campaign(), [Owner()], [Slot("Auto")], 0, 6));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/prompt-preview",
            new ImagePromptPreviewResponse(Brief, "Auto", 1280, 720, 1536, 864, 0, 0, false));

        var view = await OpenSlotAsync();

        await view.WaitForAssertionAsync(() =>
            Assert.Contains("Dark navy studio backdrop", view.Find(".cm-studio__drawer .cm-studio__brief").TextContent, StringComparison.Ordinal));
        Assert.Empty(view.FindAll(".cm-studio__drawer textarea.cm-studio__prompt"));
        Assert.Contains("Visual brief (AI-written)", view.Find(".cm-studio__drawer").TextContent, StringComparison.Ordinal);

        await view.FindAll(".cm-studio__drawer button")
            .Single(b => b.TextContent.Trim() == "Rewrite brief").ClickAsync();

        await view.WaitForAssertionAsync(() =>
            Assert.Single(Http.Bodies, b => b.Method == HttpMethod.Post && b.Path.EndsWith("/brief/rewrite", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Manual_mode_keeps_the_editable_prompt_and_offers_no_rewrite()
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(Campaign(), [Owner()], [Slot("Manual")], 0, 6));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/prompt-preview",
            new ImagePromptPreviewResponse("a bold thumbnail\n\nCOMPOSITION REQUIREMENTS", "Manual", 1280, 720, 1536, 864, 0, 0, false));

        var view = await OpenSlotAsync();

        Assert.Single(view.FindAll(".cm-studio__drawer textarea.cm-studio__prompt"));
        Assert.Empty(view.FindAll(".cm-studio__drawer .cm-studio__brief"));
        Assert.DoesNotContain(view.FindAll(".cm-studio__drawer button"), b => b.TextContent.Trim() == "Rewrite brief");
    }

    [Fact]
    public async Task The_drawer_links_back_to_the_content_item_the_image_belongs_to()
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(Campaign(), [Owner()], [Slot("Auto")], 0, 6));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/prompt-preview",
            new ImagePromptPreviewResponse(Brief, "Auto", 1280, 720, 1536, 864, 0, 0, false));

        var view = await OpenSlotAsync();

        var back = view.Find(".cm-studio__drawer-head a.cm-studio__back");
        Assert.Contains("Back to", back.TextContent, StringComparison.Ordinal);
        Assert.Contains("Grid accessibility tutorial", back.TextContent, StringComparison.Ordinal);
        Assert.Equal($"campaigns/{CampaignId}/focus?artifact={ArtifactId}", back.GetAttribute("href"));
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

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Briefed campaign", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Owner() =>
        new(ArtifactId, CampaignId, "blog", "Grid accessibility tutorial", "Draft", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ImageSlotResponse Slot(string promptMode) => new(
        SlotId, CampaignId, "content-image", 1280, 720, promptMode == "Manual" ? "a bold thumbnail" : null, null,
        null, null, SafeArea: true, "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: ArtifactId, PromptMode: promptMode);
}
