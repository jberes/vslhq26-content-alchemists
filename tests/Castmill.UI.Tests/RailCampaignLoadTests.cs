using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Layout;

namespace Castmill.UI.Tests;

/// <summary>
/// The rail is on every signed-in page, including pages that never ask for campaigns (The Wire,
/// Brands, Settings). It loads the list itself, so it is never stuck on "Loading campaigns…",
/// and it shares the request with any page that loads the list too.
/// </summary>
public sealed class RailCampaignLoadTests : CastmillUiTestContext
{
    public RailCampaignLoadTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "Reveal AI launch", null, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow),
        });
    }

    [Fact]
    public async Task The_rail_loads_the_campaigns_on_a_page_that_does_not()
    {
        var rail = Render<WorkspaceRail>();

        await rail.WaitForAssertionAsync(() =>
        {
            Assert.Empty(rail.FindAll(".cm-rail__empty.cm-loading-line"));
            Assert.Equal("Reveal AI launch", rail.Find(".cm-rail__campaign-name").TextContent);
        });
        Assert.Single(Http.Requests, request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("api/v1/campaigns", StringComparison.Ordinal));
    }
}
