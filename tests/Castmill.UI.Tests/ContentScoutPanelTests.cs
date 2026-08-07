using Bunit;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The Suggested-content panel. Two things it has to do that a plain "here are some ideas"
/// list would not: show the agent's tool calls instead of a spinner, and present "we already
/// covered this" as a real answer with the URL that proves it — rather than quietly dropping
/// it and telling the team to write the thing twice.
/// </summary>
public sealed class ContentScoutPanelTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("d1111111-1111-1111-1111-111111111111");
    private static readonly Guid TranscriptId = Guid.Parse("d1111111-1111-1111-1111-222222222222");

    public ContentScoutPanelTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [Transcript(), Plan()], [], 0, 0));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{TranscriptId}", new ArtifactResponse(
            TranscriptId, CampaignId, "transcript", "Source",
            """{"source":"paste","segments":[{"id":"S1","startSeconds":0,"endSeconds":2,"text":"Hi."}]}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{PlanId}", new ArtifactResponse(
            PlanId, CampaignId, "seo-keyword-plan", "Keyword plan", PlanJson,
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_trace_is_shown_so_the_agent_is_not_a_black_box()
    {
        StubScout(new ScoutResult(true, null,
            [new ScoutSuggestion("blog", "Governance for embedded analytics", "angle",
                ["embedded analytics governance"], "Nobody has written it", "new", [])],
            [
                new ScoutStep("search_published", "embedded analytics security", "2 source(s)"),
                new ScoutStep("search_our_drafts", "governance", "0 match(es)"),
            ],
            4200));

        var view = await ScoutAsync();

        Assert.Contains("search_published · embedded analytics security — 2 source(s)",
            view.Markup, StringComparison.Ordinal);
        Assert.Contains("search_our_drafts", view.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The most valuable outcome the Scout can produce: don't write this, we published it in
    /// June, here is the link. It must be visible AND not offer a draft button.
    /// </summary>
    [Fact]
    public async Task An_already_covered_suggestion_shows_its_evidence_and_offers_no_draft_button()
    {
        StubScout(new ScoutResult(true, null,
            [new ScoutSuggestion("blog", "Reveal 2.0 connectors", "angle", [],
                "Published in June", "covered",
                [new ScoutEvidence("Reveal 2.0", "https://www.revealbi.io/blog/reveal-2-0-release")])],
            [], 900));

        var view = await ScoutAsync();

        Assert.Contains("already covered", view.Markup, StringComparison.Ordinal);
        Assert.Contains("https://www.revealbi.io/blog/reveal-2-0-release", view.Markup, StringComparison.Ordinal);

        var item = view.Find(".cm-scout__item--covered");
        Assert.Empty(item.QuerySelectorAll("button"));
    }

    [Fact]
    public async Task Drafting_a_suggestion_sends_its_angle_and_keywords_as_the_brief()
    {
        StubScout(new ScoutResult(true, null,
            [new ScoutSuggestion("social-linkedin", "Why rollbacks matter", "Lead with the 2am story",
                ["rollback strategy", "deployment safety"], "Untouched", "new", [])],
            [], 1000));

        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/generate/social-linkedin",
            new RunItem("social-linkedin", true, Guid.NewGuid(), null, [], 1200));

        var view = await ScoutAsync();

        await view.FindAll(".cm-scout__item button")
            .First(b => b.TextContent.Contains("Draft this", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForStateAsync(
            () => Http.Bodies.Any(b => b.Path.Contains("generate/social-linkedin", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        var body = Http.Bodies.Last(b => b.Path.Contains("generate/social-linkedin", StringComparison.Ordinal)).Body;
        Assert.Contains("Why rollbacks matter", body, StringComparison.Ordinal);
        Assert.Contains("Lead with the 2am story", body, StringComparison.Ordinal);
        Assert.Contains("rollback strategy", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_scout_says_why_rather_than_showing_an_empty_list()
    {
        StubScout(new ScoutResult(false, "No model credentials configured.", [], [], 20));

        var view = await ScoutAsync();

        Assert.Contains("No model credentials configured.", view.Markup, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<SeoView>> ScoutAsync()
    {
        var view = Render<SeoView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.Markup.Contains("Suggested content", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await view.FindAll("button")
            .First(b => b.TextContent.Contains("Find opportunities", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForStateAsync(
            () => view.FindAll(".cm-scout__item").Count > 0
                  || view.Markup.Contains("credentials", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        return view;
    }

    private void StubScout(ScoutResult result) =>
        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/scout", result);

    private static readonly Guid PlanId = Guid.Parse("d1111111-1111-1111-1111-333333333333");

    private const string PlanJson = """
        {"summary":"A launch story.","focus":null,
         "youtubeTitles":["One","Two","Three"],
         "keywords":[{"term":"deployment automation","volume":1200,"difficulty":30,
                      "competition":0.4,"cpc":3.2,"source":"ai","opportunity":30.0}],
         "seoBriefArtifactId":"d1111111-1111-1111-1111-444444444444",
         "generatedAt":"2026-08-07T00:00:00+00:00"}
        """;

    private static ArtifactPreviewResponse Transcript() =>
        new(TranscriptId, CampaignId, "transcript", "Source", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Plan() =>
        new(PlanId, CampaignId, "seo-keyword-plan", "Keyword plan", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
