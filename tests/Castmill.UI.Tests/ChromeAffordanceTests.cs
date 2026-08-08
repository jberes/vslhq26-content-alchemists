using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Castmill.UI.State;

namespace Castmill.UI.Tests;

/// <summary>
/// Two affordances that were present in markup but invisible in the product, for the same
/// underlying reason twice: a Blazor bool renders as a PRESENCE-only attribute, so
/// <c>aria-selected="@(view == Current)"</c> emitted <c>aria-selected</c> with no value and
/// the stylesheet's <c>[aria-selected="true"]</c> never matched. The active campaign tab was
/// therefore indistinguishable from the other three.
/// </summary>
public sealed class ChromeAffordanceTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("a2222222-2222-2222-2222-222222222222");

    public ChromeAffordanceTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [Artifact()], [], 0, 0));
    }

    [Fact]
    public async Task The_current_view_tab_carries_the_literal_true_the_stylesheet_selects_on()
    {
        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-tabs__tab").Count == 4, TimeSpan.FromSeconds(5));

        var tabs = view.FindAll(".cm-tabs__tab");
        var selected = tabs.Where(t => t.GetAttribute("aria-selected") == "true").ToList();

        // Exactly one, and its VALUE must be the string "true" — a bare attribute would be
        // valid HTML and completely unstyled.
        var only = Assert.Single(selected);
        Assert.Equal("Mill Floor", only.TextContent.Trim());

        Assert.All(tabs.Where(t => t != only),
            t => Assert.Equal("false", t.GetAttribute("aria-selected")));
    }

    [Fact]
    public void The_stylesheet_actually_styles_the_selected_tab()
    {
        var css = Css();

        // Pinned together: the markup emitting "true" is only useful if something selects on
        // it, and the two have already drifted apart once.
        Assert.Contains("[aria-selected=\"true\"]", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card toolbar is hidden until hover, so its markup existing proves nothing about
    /// whether anyone can ever see or click it.
    /// </summary>
    [Fact]
    public void The_card_toolbar_is_revealed_on_hover_and_on_focus_and_cannot_swallow_clicks()
    {
        var css = Css();

        Assert.Contains(".cm-lane__cards > li:hover .cm-card__tools", css, StringComparison.Ordinal);
        // Keyboard users never hover; focus has to reveal it too.
        Assert.Contains(".cm-card__tools:focus-within", css, StringComparison.Ordinal);

        // An opacity:0 element still receives clicks, so the hidden toolbar would otherwise
        // intercept every click in the card's top-right corner.
        var block = css[css.IndexOf(".cm-card__tools {", StringComparison.Ordinal)..];
        block = block[..block.IndexOf('}', StringComparison.Ordinal)];
        Assert.Contains("pointer-events: none", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_card_tools_render_with_accessible_names()
    {
        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-card__tools").Count == 1, TimeSpan.FromSeconds(5));

        var tools = view.FindAll(".cm-card__tool");
        Assert.Equal(2, tools.Count);
        // The glyphs are aria-hidden, so the label is the only thing a screen reader gets.
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.GetAttribute("aria-label"))));
        Assert.Contains(tools, t => t.GetAttribute("aria-label")!.StartsWith("Edit", StringComparison.Ordinal));
        Assert.Contains(tools, t => t.GetAttribute("aria-label")!.StartsWith("Delete", StringComparison.Ordinal));
    }

    /// <summary>
    /// The mark ships beside the wordmark and inherits theme colour rather than carrying its
    /// own palette, so it works in both families and both modes.
    /// </summary>
    [Fact]
    public void The_brand_lockup_renders_the_mark_next_to_the_wordmark()
    {
        // .cm-lockup, NOT .cm-brand: that name belongs to the Brands page, and sharing it made
        // the two rules merge so the lockup inherited flex-direction: column and stacked the
        // mark above the wordmark.
        var view = Render<Castmill.UI.Layout.WorkspaceRail>();

        var brand = view.Find(".cm-lockup");
        Assert.NotNull(brand.QuerySelector("svg.cm-mark"));
        Assert.Contains("Castmill", brand.QuerySelector(".cm-wordmark")!.TextContent, StringComparison.Ordinal);

        // Decorative here: the word beside it already names the product, so announcing it
        // twice is noise to a screen reader.
        Assert.Equal("", brand.QuerySelector("svg.cm-mark")!.GetAttribute("aria-label"));

        // Theme-driven, not hardcoded — otherwise it would be wrong in one of the four
        // family x mode combinations.
        // Intrinsic size, so a missing or stale stylesheet cannot make the mark fill its
        // container and shove the wordmark onto the next line.
        var mark = brand.QuerySelector("svg.cm-mark")!;
        Assert.False(string.IsNullOrEmpty(mark.GetAttribute("width")));
        Assert.False(string.IsNullOrEmpty(mark.GetAttribute("height")));

        var svg = mark.InnerHtml;
        Assert.Contains("currentColor", svg, StringComparison.Ordinal);
        Assert.Contains("var(--cm-accent)", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("#", svg, StringComparison.Ordinal);
    }

    private static string Css()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Castmill.UI")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "Castmill.UI", "wwwroot", "css", "views.css"));
    }

    private static ArtifactPreviewResponse Artifact() =>
        new(Guid.NewGuid(), CampaignId, "blog", "A card", Castmill.Core.ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
