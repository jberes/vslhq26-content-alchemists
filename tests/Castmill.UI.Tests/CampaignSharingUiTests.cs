using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

public sealed class CampaignSharingUiTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId =
        Guid.Parse("c5555555-5555-5555-5555-555555555555");
    private static readonly Guid CollaboratorId =
        Guid.Parse("c5555555-5555-5555-5555-666666666666");

    public CampaignSharingUiTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [], [], 0, 0));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/sharing",
            new CampaignSharingResponse(false, null, []));
    }

    [Fact]
    public async Task Owner_can_enable_domain_access_and_add_a_pending_email()
    {
        var floor = Render<MillFloorView>(parameters =>
            parameters.Add(page => page.CampaignId, CampaignId));
        await floor.WaitForAssertionAsync(() =>
            Assert.NotNull(floor.Find("button[aria-label='Manage campaign sharing']")));
        var actions = floor.Find(".cm-campaign-header__actions");
        Assert.NotNull(actions.QuerySelector("button[aria-label='Rename campaign']"));
        Assert.NotNull(actions.QuerySelector("button[aria-label='Manage campaign sharing']"));
        Assert.NotNull(actions.QuerySelector("button[aria-label='Manage campaign sharing'] svg.cm-icon"));
        Assert.Equal(3, actions.QuerySelectorAll(
            "button[aria-label='Manage campaign sharing'] svg.cm-icon circle").Length);
        Assert.Null(floor.Find(".cm-campaign-header__meta")
            .QuerySelector("button[aria-label='Manage campaign sharing']"));

        await floor.Find("button[aria-label='Manage campaign sharing']").ClickAsync();
        await floor.WaitForAssertionAsync(() =>
            Assert.Contains("Share Shared campaign", floor.Markup, StringComparison.Ordinal));
        Assert.Contains("They get access when they sign in with this address.",
            floor.Markup, StringComparison.Ordinal);
        Assert.Contains("No people have been added yet.", floor.Markup, StringComparison.Ordinal);
        Assert.NotNull(floor.Find("button.cm-dialog-close"));
        Assert.NotNull(floor.Find(".cm-campaign-sharing__email"));

        Http.OnPut($"api/v1/campaigns/{CampaignId}/sharing",
            new CampaignSharingResponse(true, "example.com", []));
        await floor.Find(".cm-campaign-sharing__domain input[type=checkbox]")
            .ChangeAsync(true);
        await floor.WaitForAssertionAsync(() =>
            Assert.Contains("Anyone at example.com", floor.Markup, StringComparison.Ordinal));

        var collaborator = new CampaignCollaboratorResponse(
            CollaboratorId, "future@outside.example", null, DateTimeOffset.UtcNow);
        Http.OnPost($"api/v1/campaigns/{CampaignId}/collaborators", collaborator);
        await floor.Find("input[placeholder='person@company.com']")
            .InputAsync("future@outside.example");
        await floor.FindAll("button").Single(button => button.TextContent.Trim() == "Add")
            .ClickAsync();
        await floor.WaitForAssertionAsync(() =>
            Assert.Contains("future@outside.example", floor.Markup, StringComparison.Ordinal));

        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Put
            && request.Path.EndsWith($"campaigns/{CampaignId}/sharing", StringComparison.Ordinal)
            && request.Body.Contains("\"domainEnabled\":true", StringComparison.Ordinal));
        Assert.Contains(Http.Bodies, request =>
            request.Method == HttpMethod.Post
            && request.Path.EndsWith($"campaigns/{CampaignId}/collaborators", StringComparison.Ordinal)
            && request.Body.Contains("future@outside.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collaborator_sees_shared_marker_without_sharing_controls()
    {
        var shared = Campaign() with { IsOwner = false };
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(shared, [], [], 0, 0));

        var floor = Render<MillFloorView>(parameters =>
            parameters.Add(page => page.CampaignId, CampaignId));

        await floor.WaitForAssertionAsync(() =>
            Assert.Contains("Shared campaign", floor.Markup, StringComparison.Ordinal));
        Assert.Empty(floor.FindAll("button[aria-label='Manage campaign sharing']"));
    }

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Shared campaign", null,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1),
            Status: CampaignStatus.Draft,
            ContentType: CampaignContentType.Webinar);
}