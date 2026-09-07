using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// A citation is accepted only when it names a real approved block — but the model
/// transcribing a 32-character source guid is not where that contract should be enforced.
///
/// Observed in production (2026-09-06, clip suggestions): the model wrote
/// "evidence:b88337dc671846a986e8da611ad94df:s19" for source
/// b88337dc-6718-46a9-863e-8da611ad94df. One character dropped, so the codec rejected the
/// whole reference, the entire string became the block id, matched nothing, and a seven-item
/// press run lost a piece to a typo in a guid the model was never meant to author.
/// </summary>
public sealed class CitationRecoveryTests
{
    private static readonly Guid Source = Guid.Parse("b88337dc-6718-46a9-863e-8da611ad94df");

    [Fact]
    public void A_mis_transcribed_source_guid_still_resolves_to_the_named_block()
    {
        var context = Context(Source, "s18", "s19");

        Assert.True(context.TryNormalizeCitations(
            Json("""{"citations":["evidence:b88337dc671846a986e8da611ad94df:s19"]}"""),
            out var normalized, out var error), error);

        // Canonicalised to the true id: the artifact records real provenance, not the typo.
        var citation = normalized.GetProperty("citations")[0].GetString();
        Assert.Equal(CitationReferenceCodec.Format(Source, "s19"), citation);
    }

    [Fact]
    public void A_block_id_that_matches_nothing_is_still_refused()
    {
        var context = Context(Source, "s18", "s19");

        Assert.False(context.TryNormalizeCitations(
            Json("""{"citations":["evidence:b88337dc671846a986e8da611ad94df:s99"]}"""),
            out _, out var error));
        Assert.Contains("unknown approved evidence", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recovered_block_id_that_is_ambiguous_across_sources_is_refused()
    {
        var other = Guid.Parse("158a8801-8f24-4473-81cf-52d694cb78dc");
        var context = new GenerationEvidenceContext(
            Transcript(),
            [Block(Source, "s19"), Block(other, "s19")]);

        Assert.False(context.TryNormalizeCitations(
            Json("""{"citations":["evidence:b88337dc671846a986e8da611ad94df:s19"]}"""),
            out _, out var error));
        Assert.Contains("ambiguous", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_correct_qualified_citation_is_unaffected()
    {
        var context = Context(Source, "s18", "s19");

        Assert.True(context.TryNormalizeCitations(
            Json($$"""{"citations":["{{CitationReferenceCodec.Format(Source, "s18")}}"]}"""),
            out var normalized, out var error), error);
        Assert.Equal(CitationReferenceCodec.Format(Source, "s18"),
            normalized.GetProperty("citations")[0].GetString());
    }

    [Theory]
    [InlineData("evidence:b88337dc671846a986e8da611ad94df:s19", "s19")]
    [InlineData("evidence:not-a-guid:doc-0004", "doc-0004")]
    [InlineData("s19", "s19")]
    [InlineData("evidence:", "evidence:")]
    public void The_block_id_is_recovered_from_the_last_colon(string value, string expected) =>
        Assert.Equal(expected, GenerationEvidenceContext.RecoverBlockId(value));

    private static GenerationEvidenceContext Context(Guid source, params string[] blockIds) =>
        new(Transcript(), [.. blockIds.Select(id => Block(source, id))]);

    private static GenerationEvidenceBlock Block(Guid source, string stableId) =>
        new(source, "Recording", stableId, $"Text for {stableId}.", "media-time-range", "{}");

    private static TranscriptContent Transcript() =>
        new("test", [new TranscriptSegment("s18", 0, 5, null, "One."), new TranscriptSegment("s19", 5, 10, null, "Two.")]);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
