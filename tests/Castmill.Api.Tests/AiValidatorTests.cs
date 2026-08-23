using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

public sealed class AiValidatorTests
{
    private static readonly TranscriptContent Transcript = new("test", [
        new TranscriptSegment("S1", 0, 5, null, "We launched the new product."),
        new TranscriptSegment("S2", 5, 12, null, "It cut deployment time in half."),
        new TranscriptSegment("S3", 12, 20, null, "Customers love the new dashboard."),
    ]);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Citations_referencing_unknown_segments_fail()
    {
        var spec = Generators.Find("social-x")!;
        var result = spec.Validate(Json("""{"title":"t","text":"hi","hashtags":[],"citations":["S1","S99"]}"""), Transcript);
        Assert.False(result.Passed);
        Assert.Contains("S99", result.FatalError, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_citations_fail_the_provenance_contract()
    {
        var spec = Generators.Find("social-x")!;
        var result = spec.Validate(Json("""{"title":"t","text":"hi","hashtags":[]}"""), Transcript);
        Assert.False(result.Passed);
    }

    [Fact]
    public void Legacy_citation_is_qualified_when_it_identifies_one_approved_block()
    {
        var sourceId = Guid.NewGuid();
        var evidence = new GenerationEvidenceContext(Transcript,
        [
            new GenerationEvidenceBlock(
                sourceId, "source", "S1", "Grounded claim", "text-segment", "{}"),
        ]);

        Assert.True(evidence.TryNormalizeCitations(
            Json("""{"citations":["s1"]}"""), out var normalized, out var error), error);
        Assert.Equal(
            CitationReferenceCodec.Format(sourceId, "S1"),
            Assert.Single(normalized.GetProperty("citations").EnumerateArray()).GetString());
    }

    [Fact]
    public void Duplicate_local_ids_across_sources_require_a_qualified_citation()
    {
        var firstSource = Guid.NewGuid();
        var secondSource = Guid.NewGuid();
        var evidence = new GenerationEvidenceContext(Transcript,
        [
            new GenerationEvidenceBlock(
                firstSource, "first", "S1", "First claim", "text-segment", "{}"),
            new GenerationEvidenceBlock(
                secondSource, "second", "S1", "Second claim", "text-segment", "{}"),
        ]);

        Assert.False(evidence.TryNormalizeCitations(
            Json("""{"citations":["S1"]}"""), out _, out var ambiguous));
        Assert.Contains("ambiguous", ambiguous, StringComparison.OrdinalIgnoreCase);

        var qualified = CitationReferenceCodec.Format(secondSource, "S1");
        Assert.True(evidence.TryNormalizeCitations(
            Json($$"""{"citations":["{{qualified}}"]}"""),
            out var normalized,
            out var error), error);
        Assert.Equal(
            qualified,
            Assert.Single(normalized.GetProperty("citations").EnumerateArray()).GetString());
    }

    [Fact]
    public void Social_post_over_the_platform_cap_fails()
    {
        var spec = Generators.Find("social-x")!; // X cap = 280
        var longText = new string('x', 281);
        var result = spec.Validate(
            Json($$"""{"title":"t","text":"{{longText}}","hashtags":[],"citations":["S1"]}"""), Transcript);
        Assert.False(result.Passed);
        Assert.Contains("280", result.FatalError, StringComparison.Ordinal);
    }

    [Fact]
    public void Blog_outside_the_hard_word_band_fails_and_soft_band_warns()
    {
        var tooShort = Json("""{"title":"t","markdown":"only a few words","citations":["S1"]}""");
        Assert.False(Generators.ValidateBlog(tooShort, Transcript).Passed);

        var okButShortish = Json($$"""{"title":"t","markdown":"{{string.Join(" ", Enumerable.Repeat("word", 1000))}}","citations":["S1"]}""");
        var result = Generators.ValidateBlog(okButShortish, Transcript);
        Assert.True(result.Passed);
        Assert.NotEmpty(result.Warnings); // inside 800–3200, outside the 1500–2500 target

        var inBand = Json($$"""{"title":"t","markdown":"{{string.Join(" ", Enumerable.Repeat("word", 1800))}}","citations":["S1"]}""");
        Assert.Empty(Generators.ValidateBlog(inBand, Transcript).Warnings);
    }

    [Fact]
    public void Clip_timestamps_outside_the_source_duration_fail()
    {
        var spec = Generators.Find("clip-suggestions")!;
        var result = spec.Validate(Json("""
            {"title":"t","clips":[{"inSeconds":5,"outSeconds":500,"hook":"h","platformFit":["tiktok"]}],"citations":["S1"]}
            """), Transcript); // source ends at 20s
        Assert.False(result.Passed);
    }

    [Fact]
    public void Clips_outside_the_short_form_window_warn_but_still_pass()
    {
        var spec = Generators.Find("clip-suggestions")!;

        // 3 s and 19 s clips: both inside the source, both unusable as Shorts as-is.
        var result = spec.Validate(Json("""
            {"title":"t","clips":[
              {"inSeconds":0,"outSeconds":3,"hook":"h","platformFit":["tiktok"]},
              {"inSeconds":0,"outSeconds":19,"hook":"h","platformFit":["shorts"]}
            ],"citations":["S1"]}
            """), Transcript);

        // A bad length is a note to the human, not a reason to sink the run.
        Assert.True(result.Passed);
        Assert.Single(result.Warnings, w => w.Contains("runs 3s", StringComparison.Ordinal));
        Assert.All(result.Warnings, w => Assert.Contains("short-form window", w, StringComparison.Ordinal));
    }

    [Fact]
    public void A_clip_inside_the_short_form_window_warns_about_nothing()
    {
        var spec = Generators.Find("clip-suggestions")!;
        var result = spec.Validate(Json("""
            {"title":"t","clips":[{"inSeconds":2,"outSeconds":19,"hook":"h","clipTitle":"Deploy time, halved","platformFit":["shorts"]}],"citations":["S1"]}
            """), Transcript);

        Assert.True(result.Passed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Model_json_parses_with_or_without_code_fences()
    {
        var bare = AiOrchestrator.ParseModelJson("""{"a":1}""");
        Assert.Equal(1, bare.GetProperty("a").GetInt32());

        var fenced = AiOrchestrator.ParseModelJson("```json\n{\"a\":2}\n```");
        Assert.Equal(2, fenced.GetProperty("a").GetInt32());
    }

    /// <summary>
    /// The Tech Edit hands the model the artifact's own payload, so it has to find that
    /// payload under both shapes: the orchestrator wraps generator output as
    /// { content, validation }, while hand-authored artifacts keep their fields at the top.
    /// Feeding back the wrapper instead of the content would ask the model to edit our
    /// bookkeeping.
    /// </summary>
    [Fact]
    public void Tech_edit_reads_the_payload_from_both_artifact_shapes()
    {
        var wrapped = AiOrchestrator.ExtractContent(
            """{"content":{"title":"t","markdown":"body","citations":["S1"]},"validation":{"Passed":true,"Warnings":[]}}""");
        Assert.NotNull(wrapped);
        Assert.Equal("body", wrapped.Value.GetProperty("markdown").GetString());
        Assert.False(wrapped.Value.TryGetProperty("validation", out _));

        var flat = AiOrchestrator.ExtractContent("""{"title":"t","markdown":"body","citations":["S1"]}""");
        Assert.Equal("body", flat!.Value.GetProperty("markdown").GetString());

        Assert.Null(AiOrchestrator.ExtractContent("not json"));
        Assert.Null(AiOrchestrator.ExtractContent("[1,2,3]"));
    }

    /// <summary>
    /// A second pass runs its output through the SAME validator the first pass had to
    /// satisfy, so it can never downgrade an artifact into a state generation would have
    /// rejected — here, by dropping the citations that carry provenance.
    /// </summary>
    [Fact]
    public void A_tech_edit_that_drops_citations_fails_the_same_contract_generation_uses()
    {
        var spec = Generators.Find("social-x")!;

        var edited = Json("""{"title":"t","text":"a sharper post","hashtags":[]}""");
        Assert.False(spec.Validate(edited, Transcript).Passed);

        var kept = Json("""{"title":"t","text":"a sharper post","hashtags":[],"citations":["S1"]}""");
        Assert.True(spec.Validate(kept, Transcript).Passed);
    }

    [Theory]
    [InlineData("email-sequence", "{\"title\":\"t\",\"emails\":[{\"subject\":\"a\",\"preview\":\"p\",\"bodyMarkdown\":\"Watch: [YOUTUBE_VIDEO_URL]\"},{\"subject\":\"b\",\"preview\":\"p\",\"bodyMarkdown\":\"Follow up\"},{\"subject\":\"c\",\"preview\":\"p\",\"bodyMarkdown\":\"Last call\"}],\"citations\":[\"S1\"]}")]
    [InlineData("newsletter", "{\"title\":\"t\",\"subject\":\"s\",\"bodyMarkdown\":\"Watch: [YOUTUBE_VIDEO_URL]\",\"citations\":[\"S1\"]}")]
    public void Email_outputs_require_the_literal_esp_video_placeholder(string kind, string payload)
    {
        var spec = Generators.Find(kind)!;

        Assert.True(spec.Validate(Json(payload), Transcript).Passed);
        Assert.False(spec.Validate(Json(payload.Replace(
            Generators.EmailVideoPlaceholder, "https://youtube.com/watch?v=invented", StringComparison.Ordinal)), Transcript).Passed);
        Assert.False(spec.Validate(Json(payload.Replace(
            Generators.EmailVideoPlaceholder, "video link goes here", StringComparison.Ordinal)), Transcript).Passed);
        Assert.False(spec.Validate(Json(payload.Replace(
            Generators.EmailVideoPlaceholder,
            $"{Generators.EmailVideoPlaceholder} https://youtube.com",
            StringComparison.Ordinal)), Transcript).Passed);
    }

    [Fact]
    public void Workspace_link_substitution_does_not_invent_or_erase_the_email_video_placeholder()
    {
        var original = Json("""
            {"bodyMarkdown":"Watch: [YOUTUBE_VIDEO_URL]\n\n{{LINKS}}","citations":["S1"]}
            """);

        var substituted = AiOrchestrator.SubstituteWorkspaceLinks(
            original, "Website: https://example.com");
        var body = substituted.GetProperty("bodyMarkdown").GetString();

        Assert.Contains(Generators.EmailVideoPlaceholder, body, StringComparison.Ordinal);
        Assert.Contains("Website: https://example.com", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{LINKS}}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_ingest_produces_stable_segment_ids()
    {
        var transcript = Castmill.Api.Services.Ai.TranscriptService.FromPlainText(
            "First sentence here. Second one follows! A third, asking a question? Final thought.", "paste");
        Assert.Equal(4, transcript.Segments.Count);
        Assert.Equal(["S1", "S2", "S3", "S4"], transcript.Segments.Select(s => s.Id));
        Assert.True(transcript.Segments[3].EndSeconds > transcript.Segments[0].StartSeconds);
    }
}
