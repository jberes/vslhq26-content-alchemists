using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Castmill.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// F3's named regression test. From the design handoff: "the prototype's own bug log was
/// almost entirely 'header changed, content didn't'." Switching campaign must re-render the
/// header AND the body, from the same store, or the user reads one campaign's artifacts under
/// another campaign's name — which is worse than an error, because it looks like it worked.
/// </summary>
public sealed class CampaignSwitchTests : CastmillUiTestContext
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public CampaignSwitchTests()
    {
        SignInTestUser();

        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            Campaign(FirstId, "Webinar campaign"),
            Campaign(SecondId, "Podcast campaign"),
        });

        Http.OnGet($"api/v1/campaigns/{FirstId}/preview", Preview(
            FirstId, "Webinar campaign",
            [Artifact(FirstId, "blog", "Cutting deployment time", ArtifactStatus.InReview)],
            filled: 1, total: 6));

        Http.OnGet($"api/v1/campaigns/{SecondId}/preview", Preview(
            SecondId, "Podcast campaign",
            [Artifact(SecondId, "newsletter", "Episode 12 wrap-up", ArtifactStatus.Draft)],
            filled: 4, total: 6));
    }

    [Fact]
    public async Task Switching_campaign_re_renders_the_header_and_the_content()
    {
        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, FirstId));
        await WaitForTextAsync(view, "Cutting deployment time");

        // Sanity: the first campaign is fully rendered — name, counter and artifact.
        Assert.Contains("Webinar campaign", view.Markup, StringComparison.Ordinal);
        Assert.Contains("1/6 images", view.Markup, StringComparison.Ordinal);

        // The switch. In the app this is a route change; here it is the same thing the
        // router does — new parameters on the same component instance, which is exactly the
        // case where OnInitializedAsync would have fired once and left the body stale.
        view.Render(p => p.Add(c => c.CampaignId, SecondId));
        await WaitForTextAsync(view, "Episode 12 wrap-up");

        // Header changed...
        Assert.Contains("Podcast campaign", view.Markup, StringComparison.Ordinal);
        Assert.Contains("4/6 images", view.Markup, StringComparison.Ordinal);

        // ...AND the content changed. This is the assertion the whole test exists for.
        Assert.DoesNotContain("Cutting deployment time", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Webinar campaign", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("1/6 images", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_store_never_shows_one_campaigns_artifacts_under_another_ones_name()
    {
        var state = Services.GetRequiredService<CampaignState>();

        await state.LoadAsync(FirstId);
        Assert.Equal("Webinar campaign", state.Campaign?.Name);
        Assert.Single(state.Artifacts);

        // Assert on what SUBSCRIBERS see, not on the state between two awaits: every render
        // is driven by a Changed notification, so if no notification ever exposes a mixed
        // store, no surface can render one. (Checking the state mid-call would only work when
        // the transport is slow enough to yield — which a stub is not, and which is exactly
        // the timing assumption that lets this bug through in production.)
        var snapshots = new List<(string? Name, int Artifacts, int Slots)>();
        void Capture() => snapshots.Add((state.Campaign?.Name, state.Artifacts.Count, state.ImagesTotal));

        state.Changed += Capture;
        try
        {
            await state.LoadAsync(SecondId);
        }
        finally
        {
            state.Changed -= Capture;
        }

        // The first notification of a switch must show an empty, loading store.
        Assert.Equal((null, 0, 0), snapshots[0]);

        // And no notification anywhere may still report the campaign we switched away from.
        Assert.All(snapshots, s => Assert.True(
            s.Name is null || s.Name == "Podcast campaign",
            $"a notification during the switch still reported '{s.Name}'"));

        Assert.Equal("Podcast campaign", state.Campaign?.Name);
        Assert.Equal("Episode 12 wrap-up", Assert.Single(state.Artifacts).Title);
    }

    [Fact]
    public async Task Switching_campaign_keeps_the_current_view()
    {
        // ADR-F11: the four views are header tabs, so switching campaign is orthogonal to
        // switching view. The path builder is what guarantees it.
        var workspace = Services.GetRequiredService<WorkspaceState>();
        await workspace.LoadAsync();

        foreach (var view in Enum.GetValues<CampaignView>())
        {
            var current = CampaignViews.PathFor(FirstId, view);
            var preserved = CampaignViews.ViewFromPath(current);

            Assert.Equal(view, preserved);
            Assert.Equal(CampaignViews.PathFor(SecondId, view), CampaignViews.PathFor(SecondId, preserved));
        }
    }

    [Fact]
    public async Task The_rail_lists_every_campaign_until_the_scaling_rule_kicks_in()
    {
        var workspace = Services.GetRequiredService<WorkspaceState>();
        await workspace.LoadAsync();

        // Two campaigns: the rail lists them both and offers no index link.
        Assert.False(workspace.IsIndexed);
        Assert.Equal(2, workspace.RailCampaigns.Count());

        // Past the limit it shows only the most recent few plus the index (handoff §2).
        var many = Enumerable.Range(0, WorkspaceState.RailListLimit + 3)
            .Select(i => Campaign(Guid.NewGuid(), $"Campaign {i}"))
            .ToList();

        Http.OnGet("api/v1/campaigns", many);
        await workspace.LoadAsync(force: true);

        Assert.True(workspace.IsIndexed);
        Assert.Equal(WorkspaceState.RailRecentCount, workspace.RailCampaigns.Count());
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>
    /// bUnit's WaitForState with an explicit message: a timeout here otherwise reports
    /// "state never became true", which says nothing about which text was missing.
    /// </summary>
    private static async Task WaitForTextAsync(IRenderedComponent<MillFloorView> view, string text)
    {
        try
        {
            await view.WaitForStateAsync(
                () => view.Markup.Contains(text, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"'{text}' never rendered ({ex.GetType().Name}). Markup was:{Environment.NewLine}{view.Markup}");
        }
    }

    private static CampaignResponse Campaign(Guid id, string name) =>
        new(id, Guid.NewGuid(), name, null, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Artifact(Guid campaignId, string kind, string title, string status) =>
        new(Guid.NewGuid(), campaignId, kind, title, status, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignPreview Preview(
        Guid id, string name, ArtifactPreviewResponse[] artifacts, int filled, int total)
    {
        var slots = Enumerable.Range(0, total)
            .Select(i => new ImageSlotResponse(
                Guid.NewGuid(), id, i == 0 ? "youtube-thumbnail" : $"inline-{i}", 1280, 720,
                null, "gpt-image-2", null, null, true,
                i < filled ? "Filled" : "Empty", null, null, DateTimeOffset.UtcNow))
            .ToList();

        return new CampaignPreview(Campaign(id, name), artifacts, slots, filled, total);
    }
}
