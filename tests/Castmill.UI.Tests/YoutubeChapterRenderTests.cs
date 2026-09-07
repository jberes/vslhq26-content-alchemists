using Castmill.UI.Editor;

namespace Castmill.UI.Tests;

/// <summary>
/// YouTube chapters must render one per line and stay publishable.
///
/// Reported 2026-09-06: the description stored each chapter on its own line, exactly as
/// YouTube needs, but Focus showed them as one run-on paragraph — a single newline is a soft
/// break in Markdown. The text itself may not change: YouTube only creates a chapter from a
/// line that STARTS with the timestamp, and this same string round-trips back into the
/// published description, so a "- " bullet would silently break every chapter.
/// </summary>
public sealed class YoutubeChapterRenderTests
{
    private const string Description =
        "## Chapters\n00:00 React data grid accessibility and ARIA\n00:34 VoiceOver and React grid keyboard navigation\n01:06 Screen reader header and cell value\n\nExplore the guidance below.";

    [Fact]
    public void Chapter_lines_get_a_hard_break_and_keep_their_exact_text()
    {
        var normalized = StructuredContent.NormalizeGeneratedMarkdown(Description);

        var lines = normalized.Split('\n');
        // Every chapter but the last carries Markdown's hard break.
        Assert.Equal("00:00 React data grid accessibility and ARIA  ", lines[1]);
        Assert.Equal("00:34 VoiceOver and React grid keyboard navigation  ", lines[2]);
        Assert.Equal("01:06 Screen reader header and cell value", lines[3]);

        // The publishable text is unchanged: still one chapter per line, still timestamp-first.
        Assert.Equal(
            Description.Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal),
            normalized.Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal));
        Assert.DoesNotContain("- 00:", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_chapter_line_is_left_alone()
    {
        const string one = "## Chapters\n00:00 Only one\n\nThen prose.";
        Assert.Equal(one, StructuredContent.NormalizeGeneratedMarkdown(one));
    }

    [Fact]
    public void Prose_that_merely_mentions_a_time_is_not_treated_as_a_chapter()
    {
        const string prose = "## Chapters\nThe demo runs 00:34 into the video and then stops.\nAnother ordinary sentence.";
        Assert.Equal(prose, StructuredContent.NormalizeGeneratedMarkdown(prose));
    }

    [Fact]
    public void Applying_it_twice_changes_nothing_further()
    {
        var once = StructuredContent.NormalizeGeneratedMarkdown(Description);
        Assert.Equal(once, StructuredContent.NormalizeGeneratedMarkdown(once));
    }
}
