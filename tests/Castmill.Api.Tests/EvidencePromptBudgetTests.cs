using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;

namespace Castmill.Api.Tests;

/// <summary>
/// Observed 2026-10-10: a 57-minute webinar transcript approved as 1,067 caption segments
/// (51,722 characters) failed every generator in the run. The guard counted blocks against
/// the 1,000-block document-import cap; blog and YouTube then hid its message behind
/// "Generation failed: GenerationEvidenceException".
/// </summary>
public sealed class EvidencePromptBudgetTests
{
    private static readonly Guid Source = Guid.Parse("797e0e75-b898-4ecf-b29c-766cc5b2f847");

    [Fact]
    public void An_hour_long_transcript_of_short_segments_renders()
    {
        var context = Context(blocks: 1_067, charactersEach: 48);

        var prompt = context.ToPromptText();

        Assert.Contains("Citation ID: evidence:", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length <= GenerationEvidenceContext.MaxPromptCharacters);
    }

    [Fact]
    public void Evidence_that_renders_past_the_budget_is_refused_with_a_producer_message()
    {
        var context = Context(blocks: 50, charactersEach: 20_000);

        var ex = Assert.Throws<GenerationEvidenceException>(() => context.ToPromptText());
        Assert.Contains("exclude or split sources", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_pipeline_shows_the_evidence_refusal_instead_of_its_type_name()
    {
        var message = AiOrchestrator.FailureMessage(new GenerationEvidenceException("Too much evidence."));

        Assert.Equal("Too much evidence.", message);
    }

    [Fact]
    public void Unexpected_exceptions_still_show_only_their_type()
    {
        var message = AiOrchestrator.FailureMessage(new InvalidOperationException("secret detail"));

        Assert.Equal("Generation failed: InvalidOperationException", message);
    }

    private static GenerationEvidenceContext Context(int blocks, int charactersEach) =>
        new(
            new TranscriptContent("test", [new TranscriptSegment("s01", 0, 5, null, "One.")]),
            [.. Enumerable.Range(1, blocks).Select(i => new GenerationEvidenceBlock(
                Source,
                "Reveal - Prompt to Prod-4K.mp4",
                $"s{i:D2}",
                new string('a', charactersEach),
                "media-time-range",
                $$"""{"startSeconds":{{i * 3}},"endSeconds":{{i * 3 + 3}},"speaker":null,"sourceLabel":"Reveal - Prompt to Prod-4K.mp4"}"""))]);
}
