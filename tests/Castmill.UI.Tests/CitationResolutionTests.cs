using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Castmill.UI.State;

namespace Castmill.UI.Tests;

/// <summary>
/// The provenance threads — the connector lines from a card to the transcript rows it cites —
/// stopped drawing entirely, silently.
///
/// The data was never wrong: the artifact really did cite "S02".."S65". But a transcript that
/// came from transcription numbers its segments "s01".."s65", lower-case and zero-padded.
/// ValidateCitations compares case-INsensitively, so "S02" passes and is stored verbatim; the
/// overlay then looks the row up with <c>[data-seg="S02"]</c>, and CSS attribute selectors are
/// case-SENSITIVE. Every lookup missed, the overlay drew nothing, and nothing anywhere failed.
///
/// A silent zero is the worst failure mode there is, which is why this is pinned.
/// </summary>
public sealed class CitationResolutionTests
{
    private static IReadOnlyList<TranscriptSegment> Segments(params string[] ids) =>
        [.. ids.Select((id, i) => new TranscriptSegment(id, i * 3, (i * 3) + 3, null, $"line {i}"))];

    [Fact]
    public void Upper_case_citations_resolve_to_the_transcripts_own_lower_case_ids()
    {
        var resolved = ArtifactDisplay.ResolveCitations(
            ["S02", "S05"], Segments("s01", "s02", "s03", "s04", "s05"));

        // The canonical id, not the citation's spelling — the selector must match the DOM.
        Assert.Equal(["s02", "s05"], resolved);
    }

    [Fact]
    public void Citations_that_match_no_segment_are_dropped()
    {
        var resolved = ArtifactDisplay.ResolveCitations(
            ["s01", "s99", "nonsense"], Segments("s01", "s02"));

        // A thread to a segment that does not exist would draw a line pointing at nothing.
        Assert.Equal(["s01"], resolved);
    }

    [Fact]
    public void An_already_matching_citation_is_returned_unchanged()
    {
        var resolved = ArtifactDisplay.ResolveCitations(["S1", "S2"], Segments("S1", "S2", "S3"));

        // Pasted transcripts use "S1"; that path must not be disturbed by the fix for the other.
        Assert.Equal(["S1", "S2"], resolved);
    }

    [Fact]
    public void Qualified_evidence_citation_resolves_to_its_transcript_segment()
    {
        var sourceId = Guid.NewGuid();
        var citation = CitationReferenceCodec.Format(sourceId, "S2");

        var resolved = ArtifactDisplay.ResolveCitations(
            [citation], Segments("S1", "S2"), sourceId);

        Assert.Equal(["S2"], resolved);
    }

    [Fact]
    public void Qualified_citation_from_another_source_does_not_draw_on_the_transcript()
    {
        var transcriptSourceId = Guid.NewGuid();
        var citation = CitationReferenceCodec.Format(Guid.NewGuid(), "S2");

        var resolved = ArtifactDisplay.ResolveCitations(
            [citation], Segments("S1", "S2"), transcriptSourceId);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Order_follows_the_citation_list_so_threads_draw_in_the_cited_order()
    {
        var resolved = ArtifactDisplay.ResolveCitations(
            ["S03", "S01"], Segments("s01", "s02", "s03"));

        Assert.Equal(["s03", "s01"], resolved);
    }

    [Fact]
    public void No_transcript_yet_returns_the_citations_untouched_rather_than_nothing()
    {
        // During load the transcript can be null. Dropping every citation then would look
        // exactly like the bug this fixes.
        Assert.Equal(["S02"], ArtifactDisplay.ResolveCitations(["S02"], null));
        Assert.Equal(["S02"], ArtifactDisplay.ResolveCitations(["S02"], []));
    }

    [Fact]
    public void No_citations_is_an_empty_list_not_a_crash()
    {
        Assert.Empty(ArtifactDisplay.ResolveCitations(null, Segments("s01")));
        Assert.Empty(ArtifactDisplay.ResolveCitations([], Segments("s01")));
    }
}
