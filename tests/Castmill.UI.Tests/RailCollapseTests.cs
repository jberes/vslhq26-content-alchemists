using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Item 9: the rail collapses on demand so campaign work gets the whole window. The
/// preference is per-device (ADR-F06), applied as a root attribute exactly like the theme,
/// and restored pre-paint by both shells' index.html (live-verified, not testable here).
/// </summary>
public sealed class RailCollapseTests : CastmillUiTestContext
{
    public RailCollapseTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>());
    }

    [Fact]
    public async Task The_collapse_toggle_persists_the_preference_and_applies_the_root_attribute()
    {
        var rail = Render<WorkspaceRail>();
        var theme = Services.GetRequiredService<ThemeService>();
        await theme.InitializeAsync();

        Assert.False(theme.RailCollapsed);

        await rail.Find(".cm-rail__collapse").ClickAsync();

        Assert.True(theme.RailCollapsed);
        Assert.Equal("icons", await UiState.GetAsync("cm.rail"));
        Assert.Equal("icons", UiState.AppliedRail);

        // The button now reads as the expand affordance.
        Assert.Equal("false", rail.Find(".cm-rail__collapse").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task A_stored_preference_is_restored_on_boot()
    {
        await UiState.SetAsync("cm.rail", "icons");

        var theme = Services.GetRequiredService<ThemeService>();
        await theme.InitializeAsync();

        Assert.True(theme.RailCollapsed);
        Assert.Equal("icons", UiState.AppliedRail);
    }

    [Fact]
    public async Task Toggling_back_restores_the_responsive_default()
    {
        await UiState.SetAsync("cm.rail", "icons");
        var theme = Services.GetRequiredService<ThemeService>();
        await theme.InitializeAsync();

        await theme.ToggleRailAsync();

        Assert.False(theme.RailCollapsed);
        Assert.Null(UiState.AppliedRail);
        Assert.Equal("auto", await UiState.GetAsync("cm.rail"));
    }
}
