using System.Net;
using Bunit;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// ADR-F68/F69: in Auto mode the studio shows the written brief where the prompt would be,
/// lets the producer edit and save it directly, offers an explicit AI rewrite, and links
/// straight back to the content item the image belongs to.
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
        Http.OnStatus(HttpMethod.Put, $"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/brief", HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Auto_mode_shows_an_editable_written_brief_that_saves_and_can_be_rewritten()
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(Campaign(), [Owner()], [Slot("Auto")], 0, 6));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/prompt-preview",
            new ImagePromptPreviewResponse(Brief, "Auto", 1280, 720, 1536, 864, 0, 0, false, Brief));

        var view = await OpenSlotAsync();

        await view.WaitForAssertionAsync(() =>
            Assert.Equal(Brief, view.Find(".cm-studio__drawer textarea.cm-studio__brief").GetAttribute("value")));
        var brief = view.Find(".cm-studio__drawer textarea.cm-studio__brief");
        Assert.DoesNotContain("readonly", brief.Attributes.Select(attribute => attribute.Name));
        Assert.DoesNotContain("disabled", brief.Attributes.Select(attribute => attribute.Name));
        Assert.Contains("Visual brief (AI-written)", view.Find(".cm-studio__drawer").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Campaign.TranscriptArtifactId", view.Find(".cm-studio__drawer").TextContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(view.FindAll(".cm-studio__drawer button"),
            button => button.TextContent.Contains("Seed all prompts", StringComparison.Ordinal));

        const string edited = "Producer-edited focal point with a warmer, simpler composition.";
        await brief.InputAsync(edited);
        await brief.TriggerEventAsync("onblur", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        await view.WaitForAssertionAsync(() => Assert.Contains(Http.Bodies, body =>
            body.Method == HttpMethod.Put
            && body.Path.EndsWith("/brief", StringComparison.Ordinal)
            && body.Body.Contains(edited, StringComparison.Ordinal)));

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
            new ImagePromptPreviewResponse(Brief, "Auto", 1280, 720, 1536, 864, 0, 0, false, Brief));

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
