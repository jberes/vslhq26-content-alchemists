using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Focus mode's left rail is a vertical projection of the Mill Floor: the same lane names in
/// the same order, with non-interactive category bands and interactive content rows.
/// </summary>
public sealed class ArtifactTreeTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("41111111-1111-1111-1111-111111111111");
    private static readonly Guid BlogId = Guid.Parse("41111111-1111-1111-1111-222222222222");
    private static readonly Guid SocialId = Guid.Parse("41111111-1111-1111-1111-333333333333");
    private static readonly Guid YouTubeId = Guid.Parse("41111111-1111-1111-1111-444444444444");

    public ArtifactTreeTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });

        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [
                Artifact(BlogId, "blog", "Launch-day blog post"),
                Artifact(SocialId, "social-x", "Launch thread", BlogId),
                Artifact(YouTubeId, "youtube", "Launch video package"),
                Artifact(Guid.NewGuid(), "seo-brief", "Legacy SEO brief"),
                Artifact(Guid.NewGuid(), "seo-keyword-plan", "Keyword plan"),
                Artifact(Guid.NewGuid(), "seo-report", "Deep SEO analysis"),
            ],
            [], 0, 0));

        // Focus auto-opens the first editable artifact, so its full fetch must answer.
        StubFullArtifact(BlogId, "blog", "Launch-day blog post");
        StubFullArtifact(SocialId, "social-x", "Launch thread", BlogId);
        StubFullArtifact(YouTubeId, "youtube", "Launch video package");
    }

    [Fact]
    public async Task The_rail_mirrors_mill_floor_lanes_with_clean_noninteractive_headers()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var categories = view.FindAll(".cm-focus__category").Select(header => header.TextContent).ToList();
        Assert.Collection(categories,
            category => Assert.Contains("YouTube", category, StringComparison.Ordinal),
            category => Assert.Contains("Blog", category, StringComparison.Ordinal),
            category => Assert.Contains("Social", category, StringComparison.Ordinal));
        Assert.DoesNotContain("Social set availability", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy SEO brief", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Keyword plan", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Deep SEO analysis", view.Markup, StringComparison.Ordinal);
        Assert.Empty(view.FindAll(".cm-focus__category button"));

        // Parenthesised, so the number reads as a category count, not part of its title.
        Assert.All(view.FindAll(".cm-tree__count"), c => Assert.Matches(@"^\(\d+\)$", c.TextContent.Trim()));
    }

    [Fact]
    public async Task Entering_focus_without_a_deep_link_selects_the_first_displayed_item()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForAssertionAsync(() =>
            Assert.Equal("Launch video package", view.Find(".cm-focus__manuscript h1").TextContent));

        var firstRow = view.FindAll(".cm-focus__list-item")[0];
        Assert.Contains("Launch video package", firstRow.TextContent, StringComparison.Ordinal);
        Assert.Equal("true", firstRow.GetAttribute("aria-current"));
    }

    [Fact]
    public async Task Clicking_a_content_row_changes_the_main_manuscript()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var social = view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        await social.ClickAsync();

        await view.WaitForAssertionAsync(() =>
            Assert.Equal("Launch thread", view.Find(".cm-focus__manuscript h1").TextContent));
        Assert.Equal("true", view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal))
            .GetAttribute("aria-current"));
    }

    [Fact]
    public async Task Deleting_from_the_tree_confirms_calls_the_endpoint_and_reloads()
    {
        var confirm = new AutoConfirm(accept: true);
        Services.AddScoped<IConfirmService>(_ => confirm);

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        Http.OnStatus(HttpMethod.Delete,
            $"api/v1/campaigns/{CampaignId}/artifacts/{SocialId}", System.Net.HttpStatusCode.NoContent);

        var socialRow = view.FindAll(".cm-tree__row")
            .First(r => r.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        Assert.Contains("🗑", socialRow.QuerySelector(".cm-tree__delete")!.TextContent, StringComparison.Ordinal);
        await socialRow.QuerySelector(".cm-tree__delete")!.ClickAsync();

        Assert.Single(confirm.Requests);
        Assert.Contains(Http.Requests, r =>
            r.Method == HttpMethod.Delete
            && r.RequestUri!.AbsolutePath.EndsWith($"artifacts/{SocialId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_the_tree_delete_leaves_the_artifact_alone()
    {
        Services.AddScoped<IConfirmService>(_ => new AutoConfirm(accept: false));

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var socialRow = view.FindAll(".cm-tree__row")
            .First(r => r.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        await socialRow.QuerySelector(".cm-tree__delete")!.ClickAsync();

        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Contains("Launch thread", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Producer_only_shows_images_owned_by_the_open_content_item()
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [Artifact(BlogId, "blog", "Launch-day blog post"), Artifact(SocialId, "social-x", "Launch thread", BlogId)],
            [Slot("blog-hero", BlogId), Slot("social-card", SocialId)], 0, 2));

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Images for this content item");

        var producer = view.Find(".cm-focus__producer").TextContent;
        Assert.Contains("Blog header", producer, StringComparison.Ordinal);
        Assert.DoesNotContain("Social card", producer, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    private sealed class AutoConfirm(bool accept) : IConfirmService
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(accept);
        }
    }

    private void StubFullArtifact(Guid id, string kind, string title, Guid? parentArtifactId = null)
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{id}", new ArtifactResponse(
            id, CampaignId, kind, title, """{"content":{"markdown":"Hello."}}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            ParentArtifactId: parentArtifactId));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{id}/revisions",
            new List<ArtifactRevisionResponse>());
    }

    private static async Task WaitForTextAsync(IRenderedComponent<FocusView> view, string text)
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

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Artifact(Guid id, string kind, string title, Guid? parentArtifactId = null) =>
        new(id, CampaignId, kind, title, ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            ParentArtifactId: parentArtifactId);

    private static ImageSlotResponse Slot(string kind, Guid artifactId) => new(
        Guid.NewGuid(), CampaignId, kind, 1200, 675, null, "foundry", null, null, true,
        "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: artifactId);
}
