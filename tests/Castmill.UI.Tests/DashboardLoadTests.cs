using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Pages;
using Microsoft.AspNetCore.Components;
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
    private static readonly Guid AgingArtifact = Guid.Parse("61111111-1111-1111-1111-333333333333");
    private static readonly Guid ReviewedArtifact = Guid.Parse("61111111-1111-1111-1111-444444444444");

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
            [new DashboardArtifact(CampaignB, "Podcast campaign", AgingArtifact,
                "newsletter", "Stale October letter", ArtifactStatus.Draft, DateTimeOffset.UtcNow.AddDays(-9))],
            [
                new CampaignCounts(CampaignA, 5, 1, 2, 6,
                    Draft: 2, Reviewed: 1, Published: 1),
                new CampaignCounts(CampaignB, 3, 0, 0, 6,
                    Draft: 3, Reviewed: 0, Published: 0),
            ],
            EmptySlots: 10,
            CampaignsWithEmptySlots: 2,
            EmptySlotModels: ["gpt-image-2"],
            FirstEmptySlotCampaign: CampaignA,
            ReviewCounts: new ReviewDeskCounts(7, 1, 3, 11)));

        Http.OnGetQuery(
            "api/v1/campaigns/review-desk?status=Draft&skip=0&take=12",
            new ReviewDeskResponse(
                ArtifactStatus.Draft,
                7,
                [new DashboardArtifact(CampaignB, "Podcast campaign", Guid.NewGuid(),
                    "newsletter", "Current newsletter draft", ArtifactStatus.Draft,
                    DateTimeOffset.UtcNow)]));
        Http.OnGetQuery(
            "api/v1/campaigns/review-desk?status=Draft&skip=1&take=12",
            new ReviewDeskResponse(
                ArtifactStatus.Draft,
                7,
                [new DashboardArtifact(CampaignA, "Webinar campaign", Guid.NewGuid(),
                    "blog", "Second draft page", ArtifactStatus.Draft,
                    DateTimeOffset.UtcNow.AddMinutes(-1))]));
        Http.OnGetQuery(
            "api/v1/campaigns/review-desk?status=Queued&skip=0&take=12",
            new ReviewDeskResponse(
                ArtifactStatus.Queued,
                1,
                [new DashboardArtifact(CampaignA, "Webinar campaign", ReviewedArtifact,
                    "social-linkedin", "Scheduled launch post", ArtifactStatus.Queued,
                    DateTimeOffset.UtcNow)]));
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
        Assert.Empty(page.FindAll("nav[aria-label='Quick actions']"));

        Assert.Single(Http.Requests, r =>
            r.RequestUri!.AbsolutePath.EndsWith("campaigns/dashboard", StringComparison.Ordinal));
        Assert.DoesNotContain(Http.Requests, r =>
            r.RequestUri!.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Review_desk_shows_counts_and_loads_other_bins_on_demand()
    {
        var page = Render<FrontPage>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Cutting deployment time", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        var bins = page.FindAll(".cm-review-bin");
        Assert.Collection(bins,
            bin => Assert.Contains("Drafts7", Normalize(bin.TextContent), StringComparison.Ordinal),
            bin => Assert.Contains("Needsreview1", Normalize(bin.TextContent), StringComparison.Ordinal),
            bin => Assert.Contains("Reviewed3", Normalize(bin.TextContent), StringComparison.Ordinal),
            bin => Assert.Contains("Published11", Normalize(bin.TextContent), StringComparison.Ordinal));
        Assert.Equal("true", bins[1].GetAttribute("aria-selected"));
        Assert.DoesNotContain(Http.Requests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/review-desk", StringComparison.Ordinal));

        await bins[0].ClickAsync();
        await page.WaitForAssertionAsync(() =>
            Assert.Contains("Current newsletter draft", page.Markup, StringComparison.Ordinal));

        Assert.Contains(Http.Requests, request =>
            request.RequestUri!.PathAndQuery.EndsWith(
                "review-desk?status=Draft&skip=0&take=12", StringComparison.Ordinal));
        Assert.Equal("true", page.FindAll(".cm-review-bin")[0].GetAttribute("aria-selected"));

        await page.Find(".cm-review-desk__more").ClickAsync();
        await page.WaitForAssertionAsync(() =>
            Assert.Contains("Second draft page", page.Markup, StringComparison.Ordinal));
        Assert.Contains(Http.Requests, request =>
            request.RequestUri!.PathAndQuery.EndsWith(
                "review-desk?status=Draft&skip=1&take=12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_the_aging_list_scrolls_in_the_fixed_secondary_column()
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
        Assert.NotNull(page.Find(".cm-front__aging"));
        Assert.Equal("UL", page.Find(".cm-front__aging-list").TagName);
        Assert.NotNull(page.Find(".cm-front__aging-list[data-cm-scroll]"));
        Assert.Equal("Stale October letter",
            page.Find(".cm-front__aging-list .cm-row__label").GetAttribute("title"));
    }

    [Fact]
    public async Task Aging_draft_edit_opens_that_artifact_in_focus_mode()
    {
        var page = Render<FrontPage>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Stale October letter", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        await page.Find("button[aria-label='Edit Stale October letter']").ClickAsync();

        Assert.EndsWith(
            $"/campaigns/{CampaignB}/focus?artifact={AgingArtifact}",
            Services.GetRequiredService<NavigationManager>().Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reviewed_card_shows_its_scheduled_date_and_time_in_green()
    {
        var scheduledAt = DateTimeOffset.Now.AddDays(2).AddHours(3);
        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse>
        {
            new(Guid.NewGuid(), CampaignA, ReviewedArtifact, "linkedin", null,
                "Scheduled launch post", null, scheduledAt, "Draft", null, DateTimeOffset.UtcNow),
        });

        var page = Render<FrontPage>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Review desk", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        await page.FindAll(".cm-review-bin")[2].ClickAsync();

        await page.WaitForAssertionAsync(() =>
        {
            var scheduled = page.Find(".cm-front__scheduled");
            Assert.StartsWith("Scheduled:", scheduled.TextContent.Trim(), StringComparison.Ordinal);
                Assert.Contains(scheduledAt.ToLocalTime().ToString(
                    "MMM d, yyyy · h:mm tt", System.Globalization.CultureInfo.CurrentCulture),
                scheduled.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_campaigns_index_renders_its_counters_from_the_same_projection()
    {
        var page = Render<CampaignsIndex>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("5 artifacts", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.Contains("2/6 images", page.Markup, StringComparison.Ordinal);
        Assert.Contains("In review", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Reviewed", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Published", page.Markup, StringComparison.Ordinal);
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

    private static string Normalize(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
