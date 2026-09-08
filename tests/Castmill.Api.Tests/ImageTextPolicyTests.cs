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
    public void A_supporting_figure_fills_the_frame_while_a_headline_slot_reserves_space()
    {
        var figure = new VisualBriefRequest("content-image", 1280, 720, "Grid a11y", null, null, null, null, null, ["product"], false);
        var prompt = VisualBriefWriter.BuildPrompt(figure);
        Assert.Contains("fill the whole frame with the subject", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("where that headline will sit", prompt, StringComparison.Ordinal);
        Assert.Contains("product screenshot may be reproduced faithfully, including the text already on that screen", prompt, StringComparison.Ordinal);
        Assert.Contains("flat wireframe", prompt, StringComparison.Ordinal);

        var hero = VisualBriefWriter.BuildPrompt(figure with { SlotKind = "youtube-thumbnail", HeadlineWillBeComposited = true, ReferenceKinds = [] });
        Assert.Contains("where that headline will sit", hero, StringComparison.Ordinal);
        Assert.DoesNotContain("product screenshot may be reproduced", hero, StringComparison.Ordinal);
    }

    [Fact]
    public void Dropping_the_face_reference_renumbers_the_note_and_forbids_people()
    {
        var face = new ImageReference(Guid.NewGuid(), "face.png", "image/png", [], "face");
        var product = new ImageReference(Guid.NewGuid(), "ui.png", "image/png", [], "product");
        var background = new ImageReference(Guid.NewGuid(), "bg.png", "image/png", [], "background");
        var prompt = ImagePromptComposer.FromBrief("A dark studio scene.", [background, face, product], "warmer");

        var without = ImagePromptComposer.WithoutFaceReferences(prompt, [background, product]);

        Assert.Contains("2 reference images are attached", without, StringComparison.Ordinal);
        Assert.Contains("- Image 1: the BACKGROUND", without, StringComparison.Ordinal);
        Assert.Contains("- Image 2: the PRODUCT INTERFACE", without, StringComparison.Ordinal);
        Assert.DoesNotContain("PRESENTER", without, StringComparison.Ordinal);
        Assert.DoesNotContain("Any person shown must be", without, StringComparison.Ordinal);
        Assert.Contains("Do not show any person or face", without, StringComparison.Ordinal);
        Assert.StartsWith("A dark studio scene.", without, StringComparison.Ordinal);
        Assert.Contains("warmer", without, StringComparison.Ordinal);

        // Face only: the note disappears entirely, the brief and the instruction remain.
        var alone = ImagePromptComposer.WithoutFaceReferences(ImagePromptComposer.FromBrief("Scene.", [face]), []);
        Assert.DoesNotContain("reference image", alone, StringComparison.Ordinal);
        Assert.Contains("Do not show any person or face", alone, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_safety_refusal_with_a_face_reference_re_renders_without_it_and_says_so()
    {
        var renderer = new RefusingFacesRenderer();
        var face = new ImageReference(Guid.NewGuid(), "face.png", "image/png", [], "face");
        var product = new ImageReference(Guid.NewGuid(), "ui.png", "image/png", [], "product");
        var prompt = ImagePromptComposer.FromBrief("Scene.", [face, product]);

        var (webp, sent, note, used) = await Castmill.Api.Endpoints.ImageSlotEndpoints.RenderWithSafetyFallbackAsync(
            renderer, Guid.NewGuid(), prompt, 1280, 720, "image", [face, product], false, TestContext.Current.CancellationToken);

        Assert.NotEmpty(webp);
        Assert.Equal(2, renderer.Calls);
        Assert.Equal(Castmill.Api.Endpoints.ImageSlotEndpoints.FaceDroppedNote, note);
        Assert.Single(used);
        Assert.Equal("product", used[0].Kind);
        Assert.Contains("Do not show any person or face", sent, StringComparison.Ordinal);

        // Without a face in the mix the refusal is the producer's to see, unchanged.
        await Assert.ThrowsAsync<ImageModerationException>(() =>
            Castmill.Api.Endpoints.ImageSlotEndpoints.RenderWithSafetyFallbackAsync(
                new RefusingFacesRenderer(refuseEverything: true), Guid.NewGuid(), prompt, 1280, 720, "image", [product], false,
                TestContext.Current.CancellationToken));
    }

    private sealed class RefusingFacesRenderer(bool refuseEverything = false) : IImageRenderer
    {
        public int Calls { get; private set; }

        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<(byte[] Webp, string Prompt)> RenderExactReportingPromptAsync(
            Guid userId, string prompt, int width, int height, string? modelAlias,
            IReadOnlyList<ImageReference> references, CancellationToken ct, bool allowRenderedText = false)
        {
            Calls++;
            if (refuseEverything || references.Any(r => r.Kind == "face"))
            {
                throw new ImageModerationException("Your request was rejected by the safety system.");
            }
            return Task.FromResult((new byte[] { 1, 2, 3 }, prompt));
        }
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
