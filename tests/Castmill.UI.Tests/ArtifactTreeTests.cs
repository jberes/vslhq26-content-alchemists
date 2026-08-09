using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Focus mode's left rail is a type-grouped tree: one collapsible group per lane category,
/// items labelled with their kind, each with a delete affordance. Grouping reads the same
/// registry as the Mill Floor, so the two surfaces can never disagree about categories.
/// </summary>
public sealed class ArtifactTreeTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("41111111-1111-1111-1111-111111111111");
    private static readonly Guid BlogId = Guid.Parse("41111111-1111-1111-1111-222222222222");
    private static readonly Guid SocialId = Guid.Parse("41111111-1111-1111-1111-333333333333");

    public ArtifactTreeTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });

        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [
                Artifact(BlogId, "blog", "Launch-day blog post"),
                Artifact(SocialId, "social-x", "Launch thread"),
            ],
            [], 0, 0));

        // Focus auto-opens the first editable artifact, so its full fetch must answer.
        StubFullArtifact(BlogId, "blog", "Launch-day blog post");
        StubFullArtifact(SocialId, "social-x", "Launch thread");
    }

    [Fact]
    public async Task The_tree_groups_artifacts_by_lane_with_counts()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var heads = view.FindAll(".cm-tree__head").Select(h => h.TextContent).ToList();
        Assert.Contains(heads, h => h.Contains("Blog", StringComparison.Ordinal) && h.Contains('1'));
        Assert.Contains(heads, h => h.Contains("Social", StringComparison.Ordinal) && h.Contains('1'));

        // Parenthesised, so the number reads as a count of the section rather than as part
        // of the name — "BLOG 2" scans as a title, "BLOG (2)" as a header with a count.
        Assert.All(view.FindAll(".cm-tree__count"), c => Assert.Matches(@"^\(\d+\)$", c.TextContent.Trim()));
    }

    [Fact]
    public async Task Collapsing_a_group_hides_its_artifacts_and_flips_aria_expanded()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var socialHead = view.FindAll(".cm-tree__head")
            .First(h => h.TextContent.Contains("Social", StringComparison.Ordinal));
        Assert.Equal("true", socialHead.GetAttribute("aria-expanded"));

        await socialHead.ClickAsync();

        var collapsed = view.FindAll(".cm-tree__head")
            .First(h => h.TextContent.Contains("Social", StringComparison.Ordinal));
        Assert.Equal("false", collapsed.GetAttribute("aria-expanded"));

        // The Social item is gone from the tree; the Blog group is untouched.
        var treeItems = view.FindAll(".cm-focus__list-item").Select(i => i.TextContent).ToList();
        Assert.DoesNotContain(treeItems, i => i.Contains("Launch thread", StringComparison.Ordinal));
        Assert.Contains(treeItems, i => i.Contains("Launch-day blog post", StringComparison.Ordinal));
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
            [Artifact(BlogId, "blog", "Launch-day blog post"), Artifact(SocialId, "social-x", "Launch thread")],
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

    private void StubFullArtifact(Guid id, string kind, string title)
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{id}", new ArtifactResponse(
            id, CampaignId, kind, title, """{"content":{"markdown":"Hello."}}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
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

    private static ArtifactPreviewResponse Artifact(Guid id, string kind, string title) =>
        new(id, CampaignId, kind, title, ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static ImageSlotResponse Slot(string kind, Guid artifactId) => new(
        Guid.NewGuid(), CampaignId, kind, 1200, 675, null, "foundry", null, null, true,
        "Empty", null, null, DateTimeOffset.UtcNow, ArtifactId: artifactId);
}
