using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Item 3 of the UX overhaul: cross-campaign surfaces read ONE dashboard projection.
/// The N+1 these tests forbid (a full preview per campaign) was the front page's whole
/// G4 budget once campaigns accumulated.
/// </summary>
public sealed class DashboardLoadTests : CastmillUiTestContext
{
    private static readonly Guid CampaignA = Guid.Parse("61111111-1111-1111-1111-111111111111");
    private static readonly Guid CampaignB = Guid.Parse("61111111-1111-1111-1111-222222222222");

    public DashboardLoadTests()
    {
        SignInTestUser();

        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            Campaign(CampaignA, "Webinar campaign"),
            Campaign(CampaignB, "Podcast campaign"),
        });

        Http.OnGet("api/v1/campaigns/dashboard", new DashboardResponse(
            [new DashboardArtifact(CampaignA, "Webinar campaign", Guid.NewGuid(),
                "blog", "Cutting deployment time", ArtifactStatus.InReview, DateTimeOffset.UtcNow),
             // A stale API/cache may still return this; the client must not offer raw report
             // JSON as a Focus-mode manuscript.
             new DashboardArtifact(CampaignA, "Webinar campaign", Guid.NewGuid(),
                "seo-report", "Internal deep report", ArtifactStatus.InReview, DateTimeOffset.UtcNow)],
            [new DashboardArtifact(CampaignB, "Podcast campaign", Guid.NewGuid(),
                "newsletter", "Stale October letter", ArtifactStatus.Draft, DateTimeOffset.UtcNow.AddDays(-9))],
            [
                new CampaignCounts(CampaignA, 5, 1, 2, 6),
                new CampaignCounts(CampaignB, 3, 0, 0, 6),
            ],
            EmptySlots: 10,
            CampaignsWithEmptySlots: 2,
            EmptySlotModels: ["gpt-image-2"],
            FirstEmptySlotCampaign: CampaignA));
    }

    [Fact]
    public async Task The_front_page_renders_from_one_dashboard_call_with_no_preview_fan_out()
    {
        var page = Render<FrontPage>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Cutting deployment time", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.Contains("Stale October letter", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal deep report", page.Markup, StringComparison.Ordinal);
        Assert.Contains("10", page.Markup, StringComparison.Ordinal);
        Assert.Contains("gpt-image-2", page.Markup, StringComparison.Ordinal);

        Assert.Single(Http.Requests, r =>
            r.RequestUri!.AbsolutePath.EndsWith("campaigns/dashboard", StringComparison.Ordinal));
        Assert.DoesNotContain(Http.Requests, r =>
            r.RequestUri!.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_front_page_columns_are_their_own_scroll_regions()
    {
        // A campaign with fifty aging drafts must scroll inside the column, not grow the
        // page past the window — the structural half of that is these classes.
        var page = Render<FrontPage>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Cutting deployment time", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.NotNull(page.Find(".cm-page.cm-page--fill"));
        Assert.NotNull(page.Find(".cm-front__frame"));
        Assert.NotNull(page.Find(".cm-front__primary"));
        Assert.NotNull(page.Find(".cm-front__secondary"));
    }

    [Fact]
    public async Task The_campaigns_index_renders_its_counters_from_the_same_projection()
    {
        var page = Render<CampaignsIndex>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("5 artifacts", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.Contains("2/6 images", page.Markup, StringComparison.Ordinal);
        Assert.Contains("1 in review", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Webinar", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Thought leadership", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Http.Requests, r =>
            r.RequestUri!.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal));
    }

    private static CampaignResponse Campaign(Guid id, string name) =>
        new(id, Guid.NewGuid(), name, null, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow,
            ContentType: name.StartsWith("Webinar", StringComparison.Ordinal)
                ? CampaignContentType.Webinar
                : CampaignContentType.ThoughtLeadership);
}
