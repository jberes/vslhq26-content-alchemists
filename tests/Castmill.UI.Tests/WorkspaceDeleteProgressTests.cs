using System.Net;
using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Layout;
using Castmill.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Deleting a campaign removes every artifact, image and uploaded file and can take a while.
/// While the request is in flight the main pane is covered (blurred, blocked, and saying what
/// is happening), the campaign's rail row is dimmed and no second delete can start. It all
/// clears when the delete returns — success or failure.
/// </summary>
public sealed class WorkspaceDeleteProgressTests : CastmillUiTestContext
{
    private static readonly Guid Doomed = Guid.Parse("d7111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("d7111111-1111-1111-1111-222222222222");

    public WorkspaceDeleteProgressTests()
    {
        SignInTestUser();
        Services.AddScoped<IConfirmService>(_ => new AutoConfirm());
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            Campaign(Doomed, "Reveal AI launch"),
            Campaign(Other, "Grid accessibility"),
        });
        Http.OnGet("api/v1/campaigns/dashboard", new DashboardResponse(
            [], [], [], EmptySlots: 0, CampaignsWithEmptySlots: 0, EmptySlotModels: [], FirstEmptySlotCampaign: null));
    }

    [Fact]
    public async Task Deleting_from_the_rail_covers_the_main_pane_until_the_delete_returns()
    {
        var gate = Http.Gate(HttpMethod.Delete, $"api/v1/campaigns/{Doomed}");
        await Services.GetRequiredService<Castmill.UI.State.WorkspaceState>().LoadAsync();
        var rail = Render<WorkspaceRail>();
        var overlay = Render<WorkspaceBusyOverlay>();
        await rail.WaitForAssertionAsync(() => Assert.NotNull(rail.Find("button[aria-label='Delete Reveal AI launch']")));
        Assert.Empty(overlay.FindAll(".cm-app__busy"));

        var click = rail.Find("button[aria-label='Delete Reveal AI launch']").ClickAsync(new());

        await overlay.WaitForAssertionAsync(() =>
        {
            var busy = overlay.Find(".cm-app__busy");
            Assert.Equal("status", busy.GetAttribute("role"));
            Assert.Contains("Deleting “Reveal AI launch”", busy.TextContent, StringComparison.Ordinal);
            Assert.Matches(@"\d+s", overlay.Find(".cm-app__busy .cm-mono").TextContent);
        });
        rail.WaitForAssertion(() =>
        {
            var row = rail.FindAll(".cm-rail__row").Single(r => r.TextContent.Contains("Reveal AI launch", StringComparison.Ordinal));
            Assert.Contains("cm-rail__row--deleting", row.ClassList);
            Assert.Equal("true", row.GetAttribute("aria-busy"));
            // No second delete can start while one is running.
            Assert.All(rail.FindAll("button.cm-rail__delete"), b => Assert.True(b.HasAttribute("disabled")));
        });

        gate.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        await click;

        await overlay.WaitForAssertionAsync(() => Assert.Empty(overlay.FindAll(".cm-app__busy")));
        rail.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(rail.FindAll(".cm-rail__row"), r => r.TextContent.Contains("Reveal AI launch", StringComparison.Ordinal));
            Assert.All(rail.FindAll("button.cm-rail__delete"), b => Assert.False(b.HasAttribute("disabled")));
        });
    }

    [Fact]
    public async Task A_failed_delete_lifts_the_cover_and_keeps_the_campaign()
    {
        var gate = Http.Gate(HttpMethod.Delete, $"api/v1/campaigns/{Doomed}");
        await Services.GetRequiredService<Castmill.UI.State.WorkspaceState>().LoadAsync();
        var rail = Render<WorkspaceRail>();
        var overlay = Render<WorkspaceBusyOverlay>();
        await rail.WaitForAssertionAsync(() => Assert.NotNull(rail.Find("button[aria-label='Delete Reveal AI launch']")));

        var click = rail.Find("button[aria-label='Delete Reveal AI launch']").ClickAsync(new());
        await overlay.WaitForAssertionAsync(() => Assert.NotNull(overlay.Find(".cm-app__busy")));

        gate.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await click;

        await overlay.WaitForAssertionAsync(() => Assert.Empty(overlay.FindAll(".cm-app__busy")));
        rail.WaitForAssertion(() =>
        {
            var row = rail.FindAll(".cm-rail__row").Single(r => r.TextContent.Contains("Reveal AI launch", StringComparison.Ordinal));
            Assert.DoesNotContain("cm-rail__row--deleting", row.ClassList);
        });
    }

    [Fact]
    public async Task Deleting_from_the_campaigns_page_covers_the_main_pane_too()
    {
        var gate = Http.Gate(HttpMethod.Delete, $"api/v1/campaigns/{Doomed}");
        var page = Render<CampaignsIndex>();
        var overlay = Render<WorkspaceBusyOverlay>();
        await page.WaitForAssertionAsync(() => Assert.NotEmpty(page.FindAll("button.cm-campaign-card__delete")));

        var click = page.FindAll("button.cm-campaign-card__delete")
            .First(b => (b.GetAttribute("aria-label") ?? string.Empty).Contains("Reveal AI launch", StringComparison.Ordinal))
            .ClickAsync(new());

        await overlay.WaitForAssertionAsync(() =>
            Assert.Contains("Deleting “Reveal AI launch”", overlay.Find(".cm-app__busy").TextContent, StringComparison.Ordinal));

        gate.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        await click;
        await overlay.WaitForAssertionAsync(() => Assert.Empty(overlay.FindAll(".cm-app__busy")));
    }

    private static CampaignResponse Campaign(Guid id, string name) =>
        new(id, Guid.NewGuid(), name, null, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private sealed class AutoConfirm : IConfirmService
    {
        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);
    }
}
