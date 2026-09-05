using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;

namespace Castmill.UI.Tests;

/// <summary>The post-publish check panel (ADR-F58): URL in, verdicts + links + copies out.</summary>
public sealed class SeoDistributionPanelTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("d1570000-1111-1111-1111-111111111111");

    public SeoDistributionPanelTests()
    {
        SignInTestUser();
        Http.OnPost("api/v1/seo/distribution", new SeoDistributionReport(
            "https://www.example.com/post", DateTimeOffset.UtcNow,
            new SeoPageSnapshot("https://www.example.com/post", 200, "Blazor grids explained", "d", "https://www.example.com/post",
                ["Blazor grids explained"], ["What is a Blazor grid?", "Setup", "FAQ"], [], 1420, true, true, 98),
            [new SeoPageCheck("Canonical points here", true, "The canonical is this page."),
             new SeoPageCheck("Structured data", false, "No schema markup — add Article plus FAQPage for the FAQ section.")],
            3, [new SeoReferringDomain("medium.com", 812, 2, "2026-09-01 10:00:00 +00:00", 0)],
            "Blazor grids explained", 7,
            [new SeoMention("https://medium.com/@x/post", "medium.com", "Blazor grids explained", "Originally published…", "2026-09-02 08:00:00 +00:00", 812)],
            [new SeoSectionStatus("Page crawl", true, "HTTP 200, 1,420 words.")]));
    }

    [Fact]
    public async Task The_check_runs_against_the_pasted_url_and_shows_verdicts_links_and_copies()
    {
        var view = Render<SeoDistributionPanel>(p => p.Add(c => c.CampaignId, CampaignId).Add(c => c.DefaultUrl, "https://www.example.com"));

        var input = view.Find("input[aria-label='Published page URL']");
        Assert.Equal("https://www.example.com", input.GetAttribute("value"));
        await input.InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "https://www.example.com/post" });
        await view.Find("button.cm-button").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Path.EndsWith("seo/distribution", StringComparison.Ordinal)).Body;
            Assert.Contains("\"url\":\"https://www.example.com/post\"", body, StringComparison.Ordinal);
            Assert.Equal(2, view.FindAll("[aria-label='Page checks'] li").Count);
        });
        Assert.Single(view.FindAll("[aria-label='Page checks'] li.cm-seo__check--ok"));
        Assert.Contains("medium.com", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Mentions of “Blazor grids explained” — 7", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Referring domains — 3", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_button_waits_for_a_real_url()
    {
        var view = Render<SeoDistributionPanel>(p => p.Add(c => c.CampaignId, CampaignId));

        Assert.True(view.Find("button.cm-button").HasAttribute("disabled"));
        Assert.Empty(Http.Bodies);
    }
}
