using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// Publishing to GitHub (backend ADR-021). Two things this has to get right in the UI: an
/// optional feature nobody configured is ABSENT rather than present-and-broken (G3), and the
/// exact paths and front matter are shown before anything is committed — every tool in this
/// space that skipped that step generates support tickets.
/// </summary>
public sealed class GitPublishDialogTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    private static readonly Guid BlogId = Guid.Parse("c1111111-1111-1111-1111-222222222222");
    private static readonly Guid RepoId = Guid.Parse("c1111111-1111-1111-1111-333333333333");

    public GitPublishDialogTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [Preview()], [], 0, 0));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}", new ArtifactResponse(
            BlogId, CampaignId, "blog", "How we ship faster",
            """{"content":{"markdown":"Body."}}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/revisions",
            new List<ArtifactRevisionResponse>());
    }

    [Fact]
    public async Task With_no_repository_configured_there_is_no_publish_button_at_all()
    {
        Http.OnGet("api/v1/git/repos", new List<GitRepo>());

        var view = await OpenAsync();

        Assert.DoesNotContain(view.FindAll("button"),
            b => b.TextContent.Contains("Publish to GitHub", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_dialog_shows_the_exact_paths_and_front_matter_before_committing()
    {
        StubRepos();
        StubPreview();

        var view = await OpenAsync();
        await PublishButton(view).ClickAsync();
        await view.WaitForStateAsync(
            () => view.Markup.Contains("Front matter", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        Assert.Contains("content/posts/how-we-ship-faster.md", view.Markup, StringComparison.Ordinal);
        Assert.Contains("static/img/how-we-ship-faster/hero.webp", view.Markup, StringComparison.Ordinal);
        Assert.Contains("castmill/how-we-ship-faster", view.Markup, StringComparison.Ordinal);
        Assert.Contains("title:", view.Markup, StringComparison.Ordinal);

        // Nothing has been committed just by looking.
        Assert.DoesNotContain(Http.Requests, r =>
            r.Method == HttpMethod.Post
            && r.RequestUri!.AbsolutePath.EndsWith("/publish/github", StringComparison.Ordinal));
    }

    /// <summary>Both modes, chosen per publish — the PR is the default.</summary>
    [Fact]
    public async Task Publishing_sends_the_chosen_mode_and_reports_the_pull_request()
    {
        StubRepos();
        StubPreview();
        Http.OnPost($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/publish/github",
            new GitPublishOutcome("castmill/how-we-ship-faster", "abc1234def", 42,
                "https://github.com/acme/site/pull/42", ["content/posts/how-we-ship-faster.md"]));

        var view = await OpenAsync();
        await PublishButton(view).ClickAsync();
        await view.WaitForStateAsync(
            () => view.Markup.Contains("Open pull request", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await view.FindAll(".cm-modal__panel button")
            .First(b => b.TextContent.Contains("Open pull request", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForStateAsync(
            () => view.Markup.Contains("pull/42", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        var body = Http.Bodies.Last(b => b.Path.EndsWith("/publish/github", StringComparison.Ordinal)).Body;
        Assert.Contains("pull-request", body, StringComparison.Ordinal);
        Assert.Contains(RepoId.ToString(), body, StringComparison.OrdinalIgnoreCase);

        // The user's next action is always "open the PR", so the link has to be there.
        Assert.Contains("https://github.com/acme/site/pull/42", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_direct_commit_sends_that_mode_instead()
    {
        StubRepos();
        StubPreview();
        Http.OnPost($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/publish/github",
            new GitPublishOutcome("main", "abc1234def", null, null, ["content/posts/how-we-ship-faster.md"]));

        var view = await OpenAsync();
        await PublishButton(view).ClickAsync();
        await view.WaitForStateAsync(
            () => view.FindAll("input[name=cm-publish-mode]").Count == 2, TimeSpan.FromSeconds(5));

        view.FindAll("input[name=cm-publish-mode]")[1].Change(true);

        await view.FindAll(".cm-modal__panel button")
            .First(b => b.TextContent.Contains("Commit", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForStateAsync(
            () => Http.Bodies.Any(b => b.Path.EndsWith("/publish/github", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        var body = Http.Bodies.Last(b => b.Path.EndsWith("/publish/github", StringComparison.Ordinal)).Body;
        Assert.Contains("direct-commit", body, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<FocusView>> OpenAsync()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll("button").Any(b => b.TextContent.Contains("Download .md", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        return view;
    }

    private static AngleSharp.Dom.IElement PublishButton(IRenderedComponent<FocusView> view) =>
        view.FindAll("button").First(b => b.TextContent.Contains("Publish to GitHub", StringComparison.Ordinal));

    private void StubRepos() =>
        Http.OnGet("api/v1/git/repos", new List<GitRepo>
        {
            new(RepoId, null, "acme.dev blog", "acme", "site", null, "hugo",
                "pull-request", true, true, "{}"),
        });

    private void StubPreview() =>
        Http.OnPost($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/publish/github/preview",
            new GitPublishPreview(
                "content/posts/how-we-ship-faster.md",
                "---\ntitle: \"How we ship faster\"\ndate: \"2026-08-07\"\ndraft: true\n---\n\n",
                ["static/img/how-we-ship-faster/hero.webp"],
                "castmill/how-we-ship-faster"));

    private static ArtifactPreviewResponse Preview() =>
        new(BlogId, CampaignId, "blog", "How we ship faster", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
