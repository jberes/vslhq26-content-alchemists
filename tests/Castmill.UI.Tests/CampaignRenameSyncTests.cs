using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Layout;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// The campaign header and persistent workspace rail read different scoped stores. A rename
/// is not complete until both projections reconcile from the same server response.
/// </summary>
public sealed class CampaignRenameSyncTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId =
        Guid.Parse("c3333333-3333-3333-3333-333333333333");

    public CampaignRenameSyncTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign("Original name") });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign("Original name"), [], [], 0, 0));
    }

    [Fact]
    public async Task Rename_updates_the_header_and_campaign_rail_without_a_reload()
    {
        var floor = Render<MillFloorView>(parameters =>
            parameters.Add(page => page.CampaignId, CampaignId));
        await floor.WaitForAssertionAsync(() =>
            Assert.Equal("Original name", floor.Find(".cm-campaign-header__name").TextContent));

        var rail = Render<WorkspaceRail>();
        Assert.Equal("Original name", rail.Find(".cm-rail__campaign-name").TextContent);

        var renamed = Campaign("Renamed campaign") with { UpdatedAt = DateTimeOffset.UtcNow };
        Http.OnPut($"api/v1/campaigns/{CampaignId}", renamed);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(renamed, [], [], 0, 0));

        await floor.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Rename")
            .ClickAsync();
        await floor.Find("input[aria-label='Campaign name']")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs
            {
                Value = "Renamed campaign",
            });
        await floor.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Save")
            .ClickAsync();

        await floor.WaitForAssertionAsync(() =>
            Assert.Equal("Renamed campaign", floor.Find(".cm-campaign-header__name").TextContent));
        await rail.WaitForAssertionAsync(() =>
            Assert.Equal("Renamed campaign", rail.Find(".cm-rail__campaign-name").TextContent));

        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Put
            && request.Path.EndsWith($"campaigns/{CampaignId}", StringComparison.Ordinal)
            && request.Body.Contains("Renamed campaign", StringComparison.Ordinal));
    }

    private static CampaignResponse Campaign(string name) =>
        new(CampaignId, Guid.NewGuid(), name, null,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1),
            Status: CampaignStatus.Draft,
            ContentType: CampaignContentType.Webinar);
}
