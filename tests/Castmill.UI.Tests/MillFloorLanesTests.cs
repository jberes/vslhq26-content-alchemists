using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Castmill.UI.State;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The Mill Floor's category separation. The pre-registry board sent every unmapped kind
/// into the Social lane (the lane switch's default arm), which is how a keyword plan ended
/// up rendered between LinkedIn posts. These tests pin the registry-driven grouping, the
/// kind sub-headers, and item 8's rule: nothing image-shaped renders on the board.
/// </summary>
public sealed class MillFloorLanesTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("31111111-1111-1111-1111-111111111111");

    public MillFloorLanesTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign("Webinar campaign") });
    }

    [Fact]
    public async Task Seo_kinds_render_in_the_page_seo_lane_never_social()
    {
        StubPreview(
            Artifact("seo-keyword-plan", "Keyword plan for launch"),
            Artifact("show-notes", "Episode show notes"),
            Artifact("social-x", "Launch thread"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Keyword plan for launch");

        Assert.Equal("Page/SEO", LaneOf(view, "Keyword plan for launch"));
        Assert.Equal("Blog", LaneOf(view, "Episode show notes"));
        Assert.Equal("Social", LaneOf(view, "Launch thread"));
    }

    [Fact]
    public async Task An_unknown_kind_renders_in_the_other_lane_not_social()
    {
        StubPreview(Artifact("press-release", "Q3 press release"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Q3 press release");

        Assert.Equal("Other", LaneOf(view, "Q3 press release"));
    }

    [Fact]
    public async Task Image_prompts_and_transcript_never_render_on_the_board()
    {
        StubPreview(
            Artifact("image-prompts", "Image prompt bag"),
            Artifact("transcript", "Source transcript"),
            Artifact("blog", "The one real card"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "The one real card");

        Assert.DoesNotContain("Image prompt bag", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">Images<", view.Markup, StringComparison.Ordinal);
        var laneLabels = view.FindAll(".cm-lane__label").Select(l => l.TextContent.Trim());
        Assert.DoesNotContain("Images", laneLabels);
    }

    [Fact]
    public async Task Lanes_show_kind_subheaders_separating_each_category()
    {
        StubPreview(
            Artifact("social-x", "X launch post"),
            Artifact("social-linkedin", "LinkedIn launch post"),
            Artifact("newsletter", "October newsletter"),
            Artifact("email-sequence", "Onboarding drips"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "X launch post");

        var kindHeaders = view.FindAll(".cm-lane__kind").Select(h => h.TextContent.Trim()).ToList();
        Assert.Contains(kindHeaders, h => h.StartsWith("X post", StringComparison.Ordinal));
        Assert.Contains(kindHeaders, h => h.StartsWith("LinkedIn post", StringComparison.Ordinal));
        Assert.Contains(kindHeaders, h => h.StartsWith("Newsletter", StringComparison.Ordinal));
        Assert.Contains(kindHeaders, h => h.StartsWith("Email sequence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_print_more_menu_never_offers_image_prompts()
    {
        StubPreview(Artifact("transcript", "Source transcript"), Artifact("blog", "Blog draft"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Blog draft");

        Assert.DoesNotContain("Image prompts", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Print_more_renders_one_even_chip_per_kind_inside_a_labelled_tray()
    {
        StubPreview(Artifact("transcript", "Source transcript"), Artifact("blog", "Blog draft"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Print more from this source");

        // The label owns its own row; the actions sit on a grid, so nothing half-wraps.
        Assert.NotNull(view.Find(".cm-board__more > .cm-kicker"));
        var grid = view.Find(".cm-board__more-grid");
        var chips = grid.QuerySelectorAll(".cm-print-chip");

        // EVERY on-demand kind is offered, including ones the campaign already has: a
        // campaign with a blog is exactly the one that wants a second blog on another angle.
        Assert.Equal(8, chips.Length);
        Assert.All(chips, chip =>
        {
            Assert.NotNull(chip.QuerySelector(".cm-print-chip__plus"));
            Assert.NotNull(chip.QuerySelector(".cm-print-chip__label"));
        });
        Assert.Contains(chips, c => c.TextContent.Contains("Social set (6)", StringComparison.Ordinal));

        // The one you already own says so, so "another" has something to count from.
        var blogChip = chips.Single(c => c.TextContent.Contains("Blog", StringComparison.Ordinal));
        Assert.Equal("1", blogChip.QuerySelector(".cm-print-chip__count")!.TextContent);
    }

    [Fact]
    public async Task Deleting_a_card_confirms_then_calls_the_delete_endpoint_and_reloads()
    {
        StubPreview(Artifact("blog", "Doomed draft"));

        var confirm = new AutoConfirm(accept: true);
        Services.AddScoped<IConfirmService>(_ => confirm);

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Doomed draft");

        var artifactId = StubbedArtifacts.Single().Id;
        Http.OnStatus(HttpMethod.Delete,
            $"api/v1/campaigns/{CampaignId}/artifacts/{artifactId}", System.Net.HttpStatusCode.NoContent);

        await view.Find(".cm-card__tool--danger").ClickAsync();

        Assert.Single(confirm.Requests);
        Assert.Contains(Http.Requests, r =>
            r.Method == HttpMethod.Delete
            && r.RequestUri!.AbsolutePath.EndsWith($"artifacts/{artifactId}", StringComparison.Ordinal));
        // The reload after delete refetches the preview.
        Assert.True(Http.Requests.Count(r =>
            r.Method == HttpMethod.Get
            && r.RequestUri!.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal)) >= 2);
    }

    [Fact]
    public async Task Cancelling_the_confirm_leaves_the_artifact_alone()
    {
        StubPreview(Artifact("blog", "Spared draft"));

        Services.AddScoped<IConfirmService>(_ => new AutoConfirm(accept: false));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Spared draft");

        await view.Find(".cm-card__tool--danger").ClickAsync();

        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Contains("Spared draft", view.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editing used to need a double-click: undiscoverable, and with no keyboard or touch
    /// equivalent at all. The card's actions are now buttons on a hover toolbar.
    /// </summary>
    [Fact]
    public async Task The_edit_tool_opens_focus_on_that_artifact_in_one_click()
    {
        StubPreview(Artifact("blog", "Editable draft"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Editable draft");

        var artifactId = StubbedArtifacts.Single().Id;
        var navigation = Services.GetRequiredService<NavigationManager>();

        await view.Find(".cm-card__tools button:not(.cm-card__tool--danger)").ClickAsync();

        Assert.Contains("/focus", navigation.Uri, StringComparison.Ordinal);
        Assert.Contains($"artifact={artifactId}", navigation.Uri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deleting destroys the revision ring too, which is work the user may not realise is
    /// attached. The dialog has to say that, and its accept button must not read as the
    /// harmless default.
    /// </summary>
    [Fact]
    public async Task The_delete_confirm_names_what_is_destroyed_and_is_marked_destructive()
    {
        StubPreview(Artifact("blog", "Doomed draft"));

        var confirm = new AutoConfirm(accept: false);
        Services.AddScoped<IConfirmService>(_ => confirm);

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Doomed draft");

        await view.Find(".cm-card__tool--danger").ClickAsync();

        var request = Assert.Single(confirm.Requests);
        Assert.True(request.Destructive, "a permanent delete must be styled as destructive");
        Assert.Contains("Doomed draft", request.Message, StringComparison.Ordinal);
        Assert.Contains("revision history", request.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no undo", request.Message, StringComparison.OrdinalIgnoreCase);
        // "Delete blog post forever" — never a bare "OK" the user clicks through.
        Assert.Contains("forever", request.AcceptLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cancel", request.CancelLabel, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>Scripted confirm double: answers immediately, records every request.</summary>
    private sealed class AutoConfirm(bool accept) : IConfirmService
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(accept);
        }
    }

    private List<ArtifactPreviewResponse> StubbedArtifacts { get; } = [];

    private void StubPreview(params ArtifactPreviewResponse[] artifacts)
    {
        StubbedArtifacts.Clear();
        StubbedArtifacts.AddRange(artifacts);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign("Webinar campaign"), artifacts, [], 0, 0));

        // The store fetches the transcript artifact's full content after every preview.
        foreach (var transcript in artifacts.Where(a => a.Kind == "transcript"))
        {
            Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{transcript.Id}", new ArtifactResponse(
                transcript.Id, CampaignId, "transcript", transcript.Title,
                """{"source":"test","segments":[]}""",
                ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
        }
    }

    /// <summary>The lane label of the lane whose markup contains the given card title.</summary>
    private static string LaneOf(IRenderedComponent<MillFloorView> view, string title)
    {
        var lane = view.FindAll(".cm-lane")
            .FirstOrDefault(l => l.TextContent.Contains(title, StringComparison.Ordinal));
        Assert.NotNull(lane);
        return lane.QuerySelector(".cm-lane__label")!.TextContent.Trim();
    }

    private static async Task WaitForTextAsync(IRenderedComponent<MillFloorView> view, string text)
    {
        try
        {
            await view.WaitForStateAsync(
                () => view.Markup.Contains(text, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"'{text}' never rendered ({ex.GetType().Name}). Markup was:{Environment.NewLine}{view.Markup}");
        }
    }

    private static CampaignResponse Campaign(string name) =>
        new(CampaignId, Guid.NewGuid(), name, null, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Artifact(string kind, string title) =>
        new(Guid.NewGuid(), CampaignId, kind, title, ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
}
