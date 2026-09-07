using Castmill.UI.Editor;

namespace Castmill.UI.Tests;

/// <summary>
/// The stored YouTube description is never altered by the display normaliser (ADR-F67).
///
/// It was, once: two trailing spaces were appended to chapter lines so Markdown would break
/// them, and the editor serialized that hard break back as a backslash — putting a literal
/// "\" into the published description on the first save. Line rendering now belongs to the
/// editor (a newline is a line break, and comes back out as a newline; see
/// tests/editor-interop/line-breaks.test.js). This side of the seam must leave the text alone.
/// </summary>
public sealed class YoutubeChapterRenderTests
{
    private const string Description =
        "## Chapters\n00:00 React data grid accessibility and ARIA\n00:34 VoiceOver and React grid keyboard navigation\n01:06 Screen reader header and cell value\n\nExplore the guidance below.";

    [Fact]
    public void Chapter_lines_pass_through_byte_for_byte()
    {
        var normalized = StructuredContent.NormalizeGeneratedMarkdown(Description);

        Assert.Equal(Description, normalized);
        Assert.DoesNotMatch(@" {2}\n", normalized);
        Assert.DoesNotContain("- 00:", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_on_chapter_line_is_still_split_into_lines()
    {
        // The pre-existing repair for generators that wrote every chapter on ONE line.
        var normalized = StructuredContent.NormalizeGeneratedMarkdown("Chapters: 00:00 Intro 00:34 Setup 01:06 Demo");

        Assert.Contains("00:00 Intro", normalized, StringComparison.Ordinal);
        Assert.Contains("00:34 Setup", normalized, StringComparison.Ordinal);
        Assert.True(normalized.Split('\n').Length >= 3, normalized);
    }
}
