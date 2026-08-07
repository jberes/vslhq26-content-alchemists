using System.Text.Json;
using Castmill.Api.Services.Ai;

namespace Castmill.Api.Tests;

/// <summary>
/// Inline segment markers must not reach published copy. The prompt shows the transcript with
/// bracketed ids so the model can ground what it writes, and it fills the citations array —
/// but it also sprinkles the markers through the body, so a paragraph shipped reading
/// "…then add the required middleware. [s03][s08][s09][s17][s19][s21]".
///
/// Provenance is not lost: it lives in the citations array, which is validated against real
/// segment ids and feeds the board's provenance threads.
/// </summary>
public sealed class CitationMarkerTests
{
    [Fact]
    public void A_run_of_markers_and_the_space_before_it_is_removed()
    {
        var cleaned = CitationMarkers.Clean(
            "Configure controllers and Reveal, then add the middleware. [s03][s08][s09][s17]");

        Assert.Equal("Configure controllers and Reveal, then add the middleware.", cleaned);
    }

    [Fact]
    public void Markers_mid_sentence_do_not_leave_a_space_before_the_punctuation()
    {
        Assert.Equal(
            "It cut deploy time in half, and rollbacks are one command.",
            CitationMarkers.Clean("It cut deploy time in half [s12], and rollbacks are one command [s14]."));
    }

    [Fact]
    public void Both_id_shapes_are_recognised()
    {
        // FromPlainText emits S1; NormalizeSegments emits s01. Both are real.
        Assert.Equal("Body.", CitationMarkers.Clean("Body. [S1][S2]"));
        Assert.Equal("Body.", CitationMarkers.Clean("Body. [s01][s027]"));
    }

    /// <summary>
    /// The pattern is deliberately narrow. Stripping a markdown link or a GitHub alert marker
    /// would be a far worse bug than the one being fixed.
    /// </summary>
    [Theory]
    [InlineData("See [the docs](https://example.test) for more.")]
    [InlineData("A footnote [1] and a range [2-4].")]
    [InlineData("> [!NOTE]")]
    [InlineData("An array index like list[s] stays.")]
    [InlineData("Nothing bracketed here at all.")]
    public void Anything_that_is_not_a_segment_marker_is_left_alone(string text)
    {
        Assert.Equal(text, CitationMarkers.Clean(text));
    }

    [Fact]
    public void Prose_fields_are_cleaned_at_every_depth_and_citations_are_untouched()
    {
        using var document = JsonDocument.Parse("""
            {
              "title": "Blazor + Reveal [s01]",
              "markdown": "Add the middleware. [s03][s08]\n\nThen load a dashboard. [s27]",
              "metaDescription": "Self-service BI in Blazor [s09]",
              "emails": [
                { "subject": "Try it [s11]", "bodyMarkdown": "Here is how. [s12][s13]" }
              ],
              "citations": ["s03", "s08", "s27"]
            }
            """);

        var stripped = CitationMarkers.Strip(document.RootElement);

        Assert.Equal("Blazor + Reveal", stripped.GetProperty("title").GetString());
        Assert.Equal("Add the middleware.\n\nThen load a dashboard.",
            stripped.GetProperty("markdown").GetString());
        Assert.Equal("Self-service BI in Blazor", stripped.GetProperty("metaDescription").GetString());

        var email = stripped.GetProperty("emails")[0];
        Assert.Equal("Try it", email.GetProperty("subject").GetString());
        Assert.Equal("Here is how.", email.GetProperty("bodyMarkdown").GetString());

        // The array is the provenance record — it must survive exactly as generated, or the
        // validator and the provenance threads both lose their source of truth.
        Assert.Equal(["s03", "s08", "s27"],
            stripped.GetProperty("citations").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void A_payload_with_no_markers_is_returned_unchanged()
    {
        using var document = JsonDocument.Parse("""{"markdown":"Clean copy.","citations":["S1"]}""");
        var stripped = CitationMarkers.Strip(document.RootElement);

        Assert.Equal("Clean copy.", stripped.GetProperty("markdown").GetString());
    }

    /// <summary>
    /// The stripped body must still satisfy the validators — the word-band check counts words,
    /// and removing markers reduces the count.
    /// </summary>
    [Fact]
    public void Stripping_does_not_break_the_blog_word_band()
    {
        var words = string.Join(" ", Enumerable.Repeat("word", 1600));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            title = "t",
            markdown = words + " [s01][s02]",
            citations = new[] { "S1" },
        }));

        var stripped = CitationMarkers.Strip(document.RootElement);
        var transcript = new Castmill.Core.Ai.TranscriptContent("t", [
            new Castmill.Core.Ai.TranscriptSegment("S1", 0, 5, null, "We launched."),
        ]);

        Assert.True(Generators.ValidateBlog(stripped, transcript).Passed);
    }
}
