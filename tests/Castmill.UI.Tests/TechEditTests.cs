using Bunit;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The second pass in the Producer rail (backend ADR-020). Two things distinguish it from
/// Regenerate and both are asserted here: it revises the artifact <b>in place</b> rather than
/// printing a new row, and it never renders as a live control when no model is configured —
/// it says why instead (G3).
/// </summary>
public sealed class TechEditTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("91111111-1111-1111-1111-111111111111");
    private static readonly Guid BlogId = Guid.Parse("91111111-1111-1111-1111-222222222222");

    public TechEditTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [Preview()], [], 0, 0));
        StubArtifact(version: 1, markdown: "The first draft.");
    }

    [Fact]
    public async Task The_button_names_the_provider_that_would_run_the_second_pass()
    {
        StubStatus(configured: true, provider: new TextProviderReadiness("anthropic", true, null));

        var view = await OpenAsync();

        Assert.Contains(view.FindAll("button"),
            b => b.TextContent.Contains("Tech Edit · anthropic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_credentials_the_button_is_disabled_and_explains_itself()
    {
        StubStatus(configured: false, provider: null);

        var view = await OpenAsync();

        var button = TechEditButton(view);
        Assert.True(button.HasAttribute("disabled"));
        Assert.Contains("No model credentials configured", view.Markup, StringComparison.Ordinal);
    }

    /// <summary>A provider without a key is a warning, not a blocker: the pass runs on Foundry.</summary>
    [Fact]
    public async Task An_unkeyed_second_provider_still_allows_the_pass_but_says_it_will_use_foundry()
    {
        StubStatus(configured: true,
            provider: new TextProviderReadiness("anthropic", false, "No API key stored."));

        var view = await OpenAsync();

        Assert.False(TechEditButton(view).HasAttribute("disabled"));
        Assert.Contains("will run on Foundry instead", view.Markup, StringComparison.Ordinal);
    }

    /// <summary>The knowledge-base toggle only exists when a gateway is actually reachable.</summary>
    [Fact]
    public async Task The_knowledge_base_toggle_appears_only_when_a_gateway_is_configured()
    {
        StubStatus(configured: true, provider: new TextProviderReadiness("anthropic", true, null),
            knowledgeBase: false);
        var without = await OpenAsync();
        Assert.DoesNotContain("Consult the knowledge base", without.Markup, StringComparison.Ordinal);

        StubStatus(configured: true, provider: new TextProviderReadiness("anthropic", true, null),
            knowledgeBase: true);
        var with = await OpenAsync();
        Assert.Contains("Consult the knowledge base", with.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression that matters: the artifact id is unchanged, the manuscript shows the
    /// edited text, and the log names the provider and the changes.
    /// </summary>
    [Fact]
    public async Task A_tech_edit_revises_the_same_artifact_in_place_and_narrates_the_changes()
    {
        StubStatus(configured: true, provider: new TextProviderReadiness("anthropic", true, null));

        var view = await OpenAsync();

        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/artifacts/{BlogId}/tech-edit",
            new TechEditResult(true, null, BlogId, 2, "anthropic", true,
                ["Corrected the connector list — Reveal 2.0 adds ClickHouse (https://revealbi.io/blog)"],
                [], 1234));
        StubArtifact(version: 2, markdown: "The corrected draft.");

        await TechEditButton(view).ClickAsync();
        await view.WaitForStateAsync(
            () => view.Markup.Contains("tech edited", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        // In place: it asked for the SAME artifact back, and never reloaded the campaign to
        // hunt for a newer row the way Regenerate has to.
        var post = Http.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/tech-edit", StringComparison.Ordinal));
        Assert.Contains(BlogId.ToString(), post.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("anthropic", view.Markup, StringComparison.Ordinal);
        Assert.Contains("knowledge base consulted", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Reveal 2.0 adds ClickHouse", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Tech edited · v2", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_steering_text_and_knowledge_toggle_travel_with_the_request()
    {
        StubStatus(configured: true, provider: new TextProviderReadiness("anthropic", true, null),
            knowledgeBase: true);

        var view = await OpenAsync();

        view.Find(".cm-producer__group textarea").Change("Lead with the rollback story.");
        view.Find(".cm-producer__group input[type=checkbox]").Change(true);

        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/artifacts/{BlogId}/tech-edit",
            new TechEditResult(true, null, BlogId, 2, "anthropic", true, [], [], 10));

        await TechEditButton(view).ClickAsync();
        await view.WaitForStateAsync(
            () => Http.Bodies.Any(b => b.Path.EndsWith("/tech-edit", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        var body = Http.Bodies.Last(b => b.Path.EndsWith("/tech-edit", StringComparison.Ordinal)).Body;
        Assert.Contains("Lead with the rollback story.", body, StringComparison.Ordinal);
        Assert.Contains("\"useKnowledgeBase\":true", body, StringComparison.Ordinal);
    }

    /// <summary>A refused edit leaves the draft alone and says why.</summary>
    [Fact]
    public async Task A_rejected_tech_edit_reports_the_reason_and_does_not_touch_the_draft()
    {
        StubStatus(configured: true, provider: new TextProviderReadiness("anthropic", true, null));

        var view = await OpenAsync();

        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/artifacts/{BlogId}/tech-edit",
            new TechEditResult(false, "Tech edit rejected by validation: citation S99 is not a real segment.",
                BlogId, 1, "anthropic", false, [], [], 900));

        await TechEditButton(view).ClickAsync();
        await view.WaitForStateAsync(
            () => view.Markup.Contains("tech edit failed", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        Assert.Contains("citation S99 is not a real segment", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Tech edited · v", view.Markup, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<FocusView>> OpenAsync()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll("button").Any(b => b.TextContent.Contains("Tech Edit", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        return view;
    }

    private static AngleSharp.Dom.IElement TechEditButton(IRenderedComponent<FocusView> view) =>
        view.FindAll("button").First(b => b.TextContent.Contains("Tech Edit", StringComparison.Ordinal));

    private void StubStatus(bool configured, TextProviderReadiness? provider, bool knowledgeBase = false) =>
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            configured ? "config" : "none", configured,
            new Dictionary<string, string>(), false, null,
            [new ImageProviderReadiness("foundry", true, null)],
            provider is null ? [] : [provider],
            knowledgeBase));

    private void StubArtifact(long version, string markdown)
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}", new ArtifactResponse(
            BlogId, CampaignId, "blog", "Launch-day blog post",
            $$$"""{"content":{"markdown":"{{{markdown}}}"}}""",
            ArtifactStatus.Draft, version, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/revisions",
            new List<ArtifactRevisionResponse>());
    }

    private static ArtifactPreviewResponse Preview() =>
        new(BlogId, CampaignId, "blog", "Launch-day blog post", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
