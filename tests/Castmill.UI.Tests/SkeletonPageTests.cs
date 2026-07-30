using Bunit;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

/// <summary>
/// The shared page renders and is interactive on its own, with a fake shell behind the
/// platform seam. Neither shell is involved — that is the point of putting the UI in an RCL.
/// </summary>
public sealed class SkeletonPageTests : CastmillUiTestContext
{
    [Fact]
    public void Skeleton_page_names_the_shell_it_is_running_in()
    {
        var page = Render<Skeleton>();

        Assert.Contains("Test shell", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Headless renderer", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Skeleton_page_renders_real_ignite_ui_custom_elements()
    {
        var page = Render<Skeleton>();

        // The F0 gate is "one Ignite UI component renders", so the assertion is on a real
        // custom element in the output, not on our own markup.
        Assert.Contains("<igc-card", page.Markup, StringComparison.Ordinal);
        Assert.Contains("<igc-button", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Striking_the_plate_updates_the_count()
    {
        var page = Render<Skeleton>();
        Assert.Contains("0 struck", page.Markup, StringComparison.Ordinal);

        page.Find("igc-button").Click();

        Assert.Contains("1 struck", page.Markup, StringComparison.Ordinal);
    }
}
