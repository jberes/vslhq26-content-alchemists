using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;

namespace Castmill.UI.Tests;

public sealed class DeepSeoReportTests : CastmillUiTestContext
{
    [Fact]
    public void Full_report_renders_every_decision_section_without_hiding_missing_data()
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
                    [new SeoAeoEngineResult("chat_gpt", "ChatGPT", true, true, "An answer",
                        [new SeoCitation("Example", "https://example.com/grid", "example.com", true)])]),
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

        var view = Render<DeepSeoReport>(parameters => parameters.Add(p => p.Report, report));

        Assert.Contains("AI answer visibility", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Target keywords and opportunity", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Keyword ideas and gaps", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Keywords the site already ranks for", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Who ranks around you", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Topic visibility", view.Markup, StringComparison.Ordinal);
        Assert.Contains("62%", view.Markup, StringComparison.Ordinal);
        Assert.Contains("SERP and zero-click answer surfaces", view.Markup, StringComparison.Ordinal);
        Assert.Contains("People also ask and answer targets", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Report-grounded content opportunities", view.Markup, StringComparison.Ordinal);
        Assert.Contains("One provider unavailable", view.Markup, StringComparison.Ordinal);
    }
}
