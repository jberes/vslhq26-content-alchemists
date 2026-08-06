using Bunit;
using Bunit.TestDoubles;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The rail's Wire item used to point at a route no page served, which dead-ended the
/// user with no way back to the workspace. These tests pin that /wire is a real routed
/// page inside the shell, renders the week from the schedule mirror, and states what it
/// cannot do yet instead of offering a dead control (G3).
/// </summary>
public sealed class WirePageTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("81111111-1111-1111-1111-111111111111");
    private static readonly Guid ArtifactId = Guid.Parse("81111111-1111-1111-1111-222222222222");

    public WirePageTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
                DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow),
        });

        Http.OnGet("api/v1/campaigns/dashboard", new DashboardResponse(
            [], [], [], 0, 0, [], null,
            ReadyToSchedule:
            [
                new DashboardArtifact(CampaignId, "Webinar campaign", ArtifactId,
                    "social-x", "Launch thread", ArtifactStatus.Queued, DateTimeOffset.UtcNow),
            ]));

        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse>());
    }

    [Fact]
    public async Task The_wire_route_resolves_to_a_real_page_inside_the_shell()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/wire");

        var app = Render<App>();
        await app.WaitForStateAsync(
            () => app.Markup.Contains("Ready to schedule", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        // Not the not-found dead end...
        Assert.DoesNotContain("Nothing on this plate", app.Markup, StringComparison.Ordinal);
        // ...and the workspace rail is present, so there is always a way back.
        Assert.Contains("Front page", app.Markup, StringComparison.Ordinal);
        Assert.Contains("Campaigns", app.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_week_renders_seven_days_and_the_ready_queue()
    {
        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll(".cm-wire__day").Count == 7, TimeSpan.FromSeconds(5));

        Assert.Contains("Launch thread", page.Markup, StringComparison.Ordinal);
        // The honesty rule: the surface says scheduling itself is a later phase.
        Assert.Contains("Phase F8", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scheduled_entry_lands_in_its_day_column()
    {
        var when = DateTimeOffset.Now.Date.AddHours(9);
        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse>
        {
            new(Guid.NewGuid(), CampaignId, ArtifactId, "linkedin", null,
                "Shipping the new dashboard today.", null, when, "Queued", null, DateTimeOffset.UtcNow),
        });

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Shipping the new dashboard today.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        var today = page.FindAll(".cm-wire__day")
            .First(d => d.TextContent.Contains("Shipping the new dashboard", StringComparison.Ordinal));
        Assert.Contains("linkedin", today.TextContent, StringComparison.Ordinal);
    }
}
