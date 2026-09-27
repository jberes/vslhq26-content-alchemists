using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Layout;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

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

        await floor.Find("button[aria-label='Rename campaign']").ClickAsync();
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

    /// <summary>A rename that is in flight says so: the Save button reads "Saving…" and nothing can be clicked twice.</summary>
    [Fact]
    public async Task Saving_a_rename_shows_it_is_saving_until_the_server_answers()
    {
        var floor = Render<MillFloorView>(parameters => parameters.Add(page => page.CampaignId, CampaignId));
        await floor.WaitForAssertionAsync(() =>
            Assert.Equal("Original name", floor.Find(".cm-campaign-header__name").TextContent));
        var gate = Http.Gate(HttpMethod.Put, $"api/v1/campaigns/{CampaignId}");

        await floor.Find("button[aria-label='Rename campaign']").ClickAsync();
        await floor.Find("input[aria-label='Campaign name']")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Renamed campaign" });
        var save = floor.FindAll("button").Single(button => button.TextContent.Trim() == "Save").ClickAsync();

        await floor.WaitForAssertionAsync(() =>
        {
            var saving = floor.FindAll("button").Single(button => button.TextContent.Trim() == "Saving…");
            Assert.True(saving.HasAttribute("disabled"));
            Assert.Equal("true", saving.GetAttribute("aria-busy"));
            Assert.True(floor.Find("input[aria-label='Campaign name']").HasAttribute("disabled"));
            Assert.True(floor.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").HasAttribute("disabled"));
        });

        var renamed = Campaign("Renamed campaign") with { UpdatedAt = DateTimeOffset.UtcNow };
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(renamed, [], [], 0, 0));
        gate.SetResult(Castmill.UI.Tests.StubHttpHandler.Json(renamed));
        await save;

        await floor.WaitForAssertionAsync(() =>
            Assert.Equal("Renamed campaign", floor.Find(".cm-campaign-header__name").TextContent));
    }

    /// <summary>
    /// Before the campaign itself arrives a Save would have nothing to save against, which read
    /// as a frozen app. Rename waits for it and says why.
    /// </summary>
    [Fact]
    public async Task Rename_waits_for_the_campaign_to_load_and_says_so()
    {
        var preview = Http.Gate(HttpMethod.Get, $"api/v1/campaigns/{CampaignId}/preview");
        var floor = Render<MillFloorView>(parameters => parameters.Add(page => page.CampaignId, CampaignId));

        await floor.WaitForAssertionAsync(() =>
        {
            var rename = floor.Find("button[aria-label='Rename campaign']");
            Assert.True(rename.HasAttribute("disabled"));
            Assert.Equal("Loading the campaign…", rename.GetAttribute("title"));
        });

        preview.SetResult(Castmill.UI.Tests.StubHttpHandler.Json(new CampaignPreview(Campaign("Original name"), [], [], 0, 0)));

        await floor.WaitForAssertionAsync(() =>
        {
            var rename = floor.Find("button[aria-label='Rename campaign']");
            Assert.False(rename.HasAttribute("disabled"));
            Assert.Equal("Rename campaign", rename.GetAttribute("title"));
        });
        await floor.Find("button[aria-label='Rename campaign']").ClickAsync();
        Assert.Equal("Original name", floor.Find("input[aria-label='Campaign name']").GetAttribute("value"));
    }

    /// <summary>A rename the server refuses says why and keeps what was typed, ready to retry.</summary>
    [Fact]
    public async Task A_failed_rename_reports_the_error_and_keeps_the_typed_name()
    {
        var floor = Render<MillFloorView>(parameters => parameters.Add(page => page.CampaignId, CampaignId));
        await floor.WaitForAssertionAsync(() =>
            Assert.Equal("Original name", floor.Find(".cm-campaign-header__name").TextContent));
        var gate = Http.Gate(HttpMethod.Put, $"api/v1/campaigns/{CampaignId}");

        await floor.Find("button[aria-label='Rename campaign']").ClickAsync();
        await floor.Find("input[aria-label='Campaign name']")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Refused name" });
        var save = floor.FindAll("button").Single(button => button.TextContent.Trim() == "Save").ClickAsync();
        gate.SetResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        await save;

        await floor.WaitForAssertionAsync(() =>
        {
            var input = floor.Find("input[aria-label='Campaign name']");
            Assert.Equal("Refused name", input.GetAttribute("value"));
            Assert.False(input.HasAttribute("disabled"));
            Assert.False(floor.FindAll("button").Single(button => button.TextContent.Trim() == "Save").HasAttribute("disabled"));
        });
        Assert.Contains(Services.GetRequiredService<Castmill.UI.Design.Notifier>().Current,
            note => note.Severity == Castmill.UI.Design.NotificationSeverity.Error);
    }

    [Fact]
    public async Task Cancel_exits_rename_without_saving_the_typed_name()
    {
        var floor = Render<MillFloorView>(parameters =>
            parameters.Add(page => page.CampaignId, CampaignId));
        await floor.WaitForAssertionAsync(() =>
            Assert.Equal("Original name", floor.Find(".cm-campaign-header__name").TextContent));

        await floor.Find("button[aria-label='Rename campaign']").ClickAsync();
        await floor.Find("input[aria-label='Campaign name']").ChangeAsync(
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Discard me" });
        await floor.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Cancel")
            .ClickAsync();

        Assert.Equal("Original name", floor.Find(".cm-campaign-header__name").TextContent);
        Assert.Empty(floor.FindAll("input[aria-label='Campaign name']"));
        Assert.DoesNotContain(Http.Requests, request =>
            request.Method == HttpMethod.Put
            && request.RequestUri!.AbsolutePath.EndsWith($"campaigns/{CampaignId}", StringComparison.Ordinal));
    }

    private static CampaignResponse Campaign(string name) =>
        new(CampaignId, Guid.NewGuid(), name, null,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1),
            Status: CampaignStatus.Draft,
            ContentType: CampaignContentType.Webinar);
}
