using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Ai;

namespace Castmill.Api.Tests;

/// <summary>
/// The brand content template is labelled "authoritative … overrides conflicting generic
/// writing guidance", so nothing in the same prompt may contradict it (ADR-067). A brand whose
/// blog template said "900-1400 words" was told "Target 1500-2500 words" a few lines below,
/// and then warned by the validator for obeying either one.
/// </summary>
public sealed class BrandTemplateAuthorityTests
{
    [Fact]
    public void The_house_word_target_stands_down_when_a_blog_template_sets_the_length()
    {
        Assert.Equal("Target 1800-2600 words.", AiOrchestrator.BlogLengthRule(BrandContext.Empty));
        Assert.Equal("Target 1800-2600 words.", AiOrchestrator.BlogLengthRule(null));

        var withTemplate = BrandContext.Empty with
        {
            TemplateSteeringByKind = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["blog"] = "900-1400 words. Short paragraphs.",
            },
        };
        Assert.Equal(
            "Follow the brand content template's length; where it gives none, target 1800-2600 words.",
            AiOrchestrator.BlogLengthRule(withTemplate));
    }

    [Fact]
    public void A_template_length_is_not_warned_about_but_the_review_floor_still_holds()
    {
        var evidence = new GenerationEvidenceContext(
            new TranscriptContent("t", [new TranscriptSegment("s1", 0, 1, null, "Proof")]),
            [new GenerationEvidenceBlock(null, "t", "s1", "Proof", "legacy-transcript-segment", "{}")]);

        // 1,200 words: inside the brand's band, outside the house one.
        var draft = Blog(1200);
        Assert.Contains(
            Generators.ValidateBlog(draft, evidence).Warnings,
            w => w.Contains("target band is 1500–2500", StringComparison.Ordinal));
        Assert.Empty(Generators.ValidateBlog(draft, evidence, lengthFromTemplate: true).Warnings);

        // The floor is a review guard, not a preference: a template cannot wave it through.
        var tiny = Generators.ValidateBlog(Blog(200), evidence, lengthFromTemplate: true);
        Assert.False(tiny.Passed);
        Assert.Contains("800–3200", tiny.FatalError, StringComparison.Ordinal);
    }

    private static JsonElement Blog(int words)
    {
        var markdown = string.Join(' ', Enumerable.Repeat("word", words));
        return JsonDocument.Parse(
            $$"""{"title":"T","markdown":"{{markdown}}","metaDescription":"d","citations":["s1"]}""")
            .RootElement.Clone();
    }
}
