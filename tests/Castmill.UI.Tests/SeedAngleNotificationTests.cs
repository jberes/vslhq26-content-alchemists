using System.Net;
using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// "Seed blog from this angle" is background work: the card says it is being written, and when
/// the blog lands the producer is told — wherever they are — with a link to it in Focus mode.
/// </summary>
public sealed class SeedAngleNotificationTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("5eed1111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = Guid.Parse("5eed1111-1111-1111-1111-222222222222");

    public SeedAngleNotificationTests() => SignInTestUser();

    [Fact]
    public async Task A_seeded_blog_that_lands_posts_a_ready_notification_linking_to_it_in_focus()
    {
        var artifactId = Guid.NewGuid();
        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/generate",
            new RunFinished(RunId, 1, 0, [new RunItem("blog", true, artifactId, null, null, 60_000)]));

        await RunAndWaitAsync("the blog from “Faster grids”");

        var note = Assert.Single(Services.GetRequiredService<Notifier>().Current);
        Assert.Equal("The blog from “Faster grids” is ready.", note.Message);
        Assert.Equal("Open in Focus mode", note.ActionLabel);
        Assert.Equal($"campaigns/{CampaignId}/focus?artifact={artifactId}", note.ActionHref);
        Assert.Equal(Timeout.InfiniteTimeSpan, note.Duration);
    }

    [Fact]
    public async Task A_seeded_blog_that_fails_says_so_and_why()
    {
        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/generate",
            new RunFinished(RunId, 0, 1, [new RunItem("blog", false, null, "The model refused the brief.", null, 9_000)]));

        await RunAndWaitAsync("the blog from “Faster grids”");

        var note = Assert.Single(Services.GetRequiredService<Notifier>().Current);
        Assert.Equal(NotificationSeverity.Error, note.Severity);
        Assert.Contains("could not be written", note.Message, StringComparison.Ordinal);
        Assert.Contains("The model refused the brief.", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_started_without_an_announcement_stays_silent()
    {
        Http.OnPost($"api/v1/ai/campaigns/{CampaignId}/generate",
            new RunFinished(RunId, 1, 0, [new RunItem("blog", true, Guid.NewGuid(), null, null, 60_000)]));

        await RunAndWaitAsync(null);

        Assert.Empty(Services.GetRequiredService<Notifier>().Current);
    }

    [Fact]
    public void The_toast_host_renders_the_action_and_keeps_the_toast_until_dismissed()
    {
        var notifier = Services.GetRequiredService<Notifier>();
        var host = Render<NotificationHost>();

        host.InvokeAsync(() => notifier.ShowReady("The blog is ready.", "Open in Focus mode", "campaigns/x/focus?artifact=y"));

        host.WaitForAssertion(() =>
        {
            var link = host.Find(".cm-toast__action");
            Assert.Equal("Open in Focus mode", link.TextContent);
            Assert.Equal("campaigns/x/focus?artifact=y", link.GetAttribute("href"));
            Assert.NotNull(host.Find(".cm-toast__dismiss"));
        });
    }

    [Fact]
    public void The_seeded_angle_says_it_is_being_written_and_the_others_wait()
    {
        var report = new SeoAnalysisReportResponse(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            new SeoResearchResponse(
                [new SeoTarget("react data grid", 8100, 42, 157.7, "provider", .38, 4.2, "commercial")],
                [new SeoQuestion("How do you paginate a React data grid?", "paa")], true, []),
            new SeoSerpSnapshot("react data grid", "AI overview text", "Featured answer",
                [new SeoSerpResult(1, "Top result", "https://leader.example/grid", "leader.example", "Fast grid guide")]),
            ["Lead with a direct answer."],
            SiteUrl: "https://example.com",
            Insights: new SeoDeepInsights(
                new SeoAeoScorecard(50, 4, 2,
                    [new SeoAeoEngineResult("chat_gpt", "ChatGPT", true, true,
                        "## Recommended answer\n\n- First point",
                        [new SeoCitation("Example", "https://example.com/grid", "example.com", true)]),
                     new SeoAeoEngineResult("gemini", "Gemini", true, false,
                        "**Gemini answer** with a different framing.", [])]),
                [new SeoTarget("react grid export", 900, 18, 32, "provider", .2, 2.1, "commercial")],
                [new SeoRankedKeyword("existing grid query", 6, 1200, 25, 80, "https://example.com/existing", "informational")],
                new SeoAuthoritySnapshot("example.com", 45, 4000, 220, 180, 3, 2),
                [new SeoCompetitorSnapshot("example.com", 0,
                    new SeoAuthoritySnapshot("example.com", 45, 4000, 220, 180, 3, 2),
                    new SeoPositionFootprint(4, 12, 40, 220, 500), true,
                    TopicKeywordCount: 3, TopicVisibility: .12,
                    TopicEstimatedTraffic: 40, TopicAveragePosition: 8),
                 new SeoCompetitorSnapshot("leader.example", 1,
                    new SeoAuthoritySnapshot("leader.example", 70, 18000, 900, 750, 10, 1),
                    new SeoPositionFootprint(30, 80, 190, 1200, 9000),
                    TopicKeywordCount: 8, TopicVisibility: .62,
                    TopicEstimatedTraffic: 440, TopicAveragePosition: 2.4)],
                [new SeoContentAngle("Export without blocking the UI", "A practical answer",
                    "Tutorial", "react grid export", "Competitors do not cover the failure mode.")],
                [new SeoSectionStatus("Live search data", true, "Captured live data."),
                 new SeoSectionStatus("AEO visibility", false, "One provider unavailable.")],
                DateTimeOffset.UtcNow));
        var angle = report.Insights!.ContentAngles[0].Angle;

        var view = Render<DeepSeoReport>(p => p
            .Add(r => r.Report, report)
            .Add(r => r.SeedingAngle, angle)
            .Add(r => r.OnDraftAngle, (SeoContentAngle _) => { }));

        var button = view.FindAll("button").Single(b => b.TextContent.Contains("Writing in the background…", StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("true", button.GetAttribute("aria-busy"));
        Assert.Contains("notification with a link when it's ready in Focus mode", view.Find(".cm-report-angle__seeding").TextContent, StringComparison.Ordinal);
    }

    private async Task RunAndWaitAsync(string? announcement)
    {
        var press = Services.GetRequiredService<PressRunService>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        press.Changed += () => { if (!press.IsRunning) finished.TrySetResult(); };
        press.Start(CampaignId, null, "brief", ["blog"], announcement: announcement);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
