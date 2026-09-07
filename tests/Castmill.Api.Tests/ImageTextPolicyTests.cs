using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Images;

namespace Castmill.Api.Tests;

/// <summary>
/// ADR-075: who may put words in a picture. One decision, in code, so no prompt can widen it.
/// No database, no Docker.
/// </summary>
public sealed class ImageTextPolicyTests
{
    [Theory]
    [InlineData("youtube-thumbnail", null, true, true)]
    [InlineData("social-card", "", true, true)]
    [InlineData("youtube-thumbnail", "REACT GRID", true, false)] // a configured headline is composited, never spelled
    [InlineData("youtube-thumbnail", null, false, false)]        // a model that cannot spell paints no text
    [InlineData("blog-hero", null, true, false)]                 // not a text-first kind
    [InlineData("og-image", null, true, false)]
    [InlineData(null, null, true, false)]
    public void Only_a_text_first_slot_without_a_headline_on_a_spelling_model_may_render_words(
        string? kind, string? headline, bool modelRendersText, bool expected) =>
        Assert.Equal(expected, ImagePromptRules.AllowsRenderedText(kind, headline, modelRendersText));

    [Fact]
    public void The_rules_block_swaps_the_no_text_bullet_for_an_exact_quoted_text_bullet()
    {
        var rules = ImagePromptRules.Apply("A dark navy studio backdrop.", 1280, 720, 1536, 864);
        Assert.Contains("Do not render any new text", rules, StringComparison.Ordinal);

        var allowed = ImagePromptRules.WithRenderedTextAllowed(rules);

        Assert.DoesNotContain("Do not render any new text", allowed, StringComparison.Ordinal);
        Assert.Contains("Render ONLY the text the brief", allowed, StringComparison.Ordinal);
        Assert.Contains("spelled exactly", allowed, StringComparison.Ordinal);
        // The safe-margin rule is untouched: text may exist, but never near an edge.
        Assert.Contains("Never let a letter, word, or subject touch", allowed, StringComparison.Ordinal);
        Assert.StartsWith("A dark navy studio backdrop.", allowed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_brief_writer_is_told_the_asset_the_frame_and_whether_text_is_allowed()
    {
        var request = new VisualBriefRequest(
            SlotKind: "youtube-thumbnail", TargetWidth: 1280, TargetHeight: 720,
            Subject: "React Grid accessibility that speaks",
            ContentDigest: "Screen readers announce every cell; keyboard users never get trapped.",
            CampaignBrief: "Launch week for the grid.",
            CreativeDirection: "Dark, cinematic, one bold headline.",
            BrandLook: "Palette: navy #0B1F3A, cyan #19D3F5.",
            Audience: "Front-end developers",
            ReferenceKinds: ["background", "face"],
            TextMayBeRendered: true);

        var prompt = VisualBriefWriter.BuildPrompt(request);

        Assert.Contains("1280×720", prompt, StringComparison.Ordinal);
        Assert.Contains("Text in the image: ALLOWED", prompt, StringComparison.Ordinal);
        Assert.Contains("quotation marks", prompt, StringComparison.Ordinal);
        Assert.Contains("Attached references, in order: background, face", prompt, StringComparison.Ordinal);
        Assert.Contains("Audience: Front-end developers", prompt, StringComparison.Ordinal);
        Assert.Contains("Subject: React Grid accessibility that speaks", prompt, StringComparison.Ordinal);
        Assert.Contains("Producer's direction (honour it): Dark, cinematic, one bold headline.", prompt, StringComparison.Ordinal);
        Assert.Contains("navy #0B1F3A", prompt, StringComparison.Ordinal);
        Assert.Contains("ONE hook", prompt, StringComparison.Ordinal);
        Assert.Contains("Output ONLY the final image-generation prompt", prompt, StringComparison.Ordinal);

        var noText = VisualBriefWriter.BuildPrompt(request with { TextMayBeRendered = false, ReferenceKinds = [] });
        Assert.Contains("Text in the image: NOT allowed", noText, StringComparison.Ordinal);
        Assert.DoesNotContain("Attached references", noText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brief_carries_only_the_reference_roles_and_the_adjustment_when_composed()
    {
        var brief = "Dark navy backdrop, a simplified data grid floating centre-left, cyan glow.";
        var references = new List<ImageReference>
        {
            new(Guid.NewGuid(), "bg.png", "image/png", [], "background"),
            new(Guid.NewGuid(), "face.png", "image/png", [], "face"),
        };

        var prompt = ImagePromptComposer.FromBrief(brief, references, "make the glow warmer");

        Assert.StartsWith(brief, prompt, StringComparison.Ordinal);
        Assert.Contains("2 reference images are attached", prompt, StringComparison.Ordinal);
        Assert.Contains("- Image 1: the BACKGROUND", prompt, StringComparison.Ordinal);
        Assert.Contains("- Image 2: the PRESENTER", prompt, StringComparison.Ordinal);
        Assert.Contains("make the glow warmer", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("COMPOSITION REQUIREMENTS", prompt, StringComparison.Ordinal); // the renderer's job
        Assert.Equal(brief, ImagePromptComposer.FromBrief(brief));
    }
}
