using Castmill.Api.Endpoints;

namespace Castmill.Api.Tests;

/// <summary>
/// The auto image prompt's content block (ADR-054). It used to paste up to 5,000 characters
/// of the artifact's raw JSON — field names, markdown syntax, citation ids — into the
/// prompt, and the model illustrated the noise. No database, no Docker.
/// </summary>
public sealed class ImageAutoPromptTests
{
    private const string Envelope = """
        {
          "content": {
            "title": "AI coding agents need structured UI knowledge",
            "summary": "Why component metadata beats scraped docs for **enterprise** React teams.",
            "markdown": "# Why agents guess\n\nWithout structured component knowledge an agent reproduces whatever pattern it saw last, and enterprise React apps pay for it in review cycles. [[cite:seg-12]]\n\n## What structured knowledge looks like\n\nSome body text.\n\n## Try it\n\n![hero](https://x/y.png)",
            "citations": ["seg-12", "seg-40"]
          },
          "validation": { "passed": true, "warnings": [] }
        }
        """;

    [Fact]
    public void The_digest_keeps_titles_summaries_and_headings_and_drops_markup_and_ids()
    {
        var digest = ImageSlotEndpoints.ContentDigest(Envelope)!;

        Assert.Contains("AI coding agents need structured UI knowledge", digest, StringComparison.Ordinal);
        Assert.Contains("enterprise React teams", digest, StringComparison.Ordinal);
        Assert.Contains("Sections: Why agents guess · What structured knowledge looks like · Try it", digest, StringComparison.Ordinal);
        Assert.Contains("Without structured component knowledge", digest, StringComparison.Ordinal);

        Assert.DoesNotContain("citations", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("seg-12", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("**", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("![", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("{", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("validation", digest, StringComparison.Ordinal);
    }

    [Fact]
    public void The_digest_is_capped_and_survives_payloads_that_are_not_json()
    {
        var digest = ImageSlotEndpoints.ContentDigest(new string('x', 5000), maxChars: 100)!;
        Assert.True(digest.Length <= 101, "digest must be capped");
        Assert.EndsWith("…", digest, StringComparison.Ordinal);

        Assert.Null(ImageSlotEndpoints.ContentDigest(null));
        Assert.Null(ImageSlotEndpoints.ContentDigest("   "));
    }
}
