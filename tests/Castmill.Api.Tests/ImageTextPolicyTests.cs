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
    [InlineData("content-image", null, true, true)]              // ADR-085: a scene's own quoted labels
    [InlineData("content-image", "Composited headline", true, true)] // labels yes; the brief forbids a painted title
    [InlineData("content-image", null, false, false)]
    [InlineData("blog-inline-1", null, false, false)]
    public void Only_a_spelling_model_may_render_words_and_never_over_a_composited_thumbnail_title(
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
    public void The_rules_swap_accepts_windows_line_endings()
    {
        var rules = ImagePromptRules
            .Apply("A dark navy studio backdrop.", 1280, 720, 1536, 864)
            .ReplaceLineEndings("\r\n");

        var allowed = ImagePromptRules.WithRenderedTextAllowed(rules);

        Assert.DoesNotContain("Do not render any new text", allowed, StringComparison.Ordinal);
        Assert.Contains("Render ONLY the text the brief", allowed, StringComparison.Ordinal);
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
            TextMayBeRendered: true,
            BrandContentTemplate: "Titles must name the demonstrated feature and concrete payoff.");

        var prompt = VisualBriefWriter.BuildPrompt(request);

        Assert.Contains("1280×720", prompt, StringComparison.Ordinal);
        Assert.Contains("Text in the image: REQUIRED", prompt, StringComparison.Ordinal);
        Assert.Contains("quotation marks", prompt, StringComparison.Ordinal);
        Assert.Contains("Attached references, in order: background, face", prompt, StringComparison.Ordinal);
        Assert.Contains("Audience: Front-end developers", prompt, StringComparison.Ordinal);
        Assert.Contains("Title: React Grid accessibility that speaks", prompt, StringComparison.Ordinal);
        Assert.Contains("Producer's direction (honour it): Dark, cinematic, one bold headline.", prompt, StringComparison.Ordinal);
        Assert.Contains("navy #0B1F3A", prompt, StringComparison.Ordinal);
        Assert.Contains("specific to the actual subject, feature, problem or payoff", prompt, StringComparison.Ordinal);
        Assert.Contains("AUTHORITATIVE BRAND CONTENT TEMPLATE", prompt, StringComparison.Ordinal);
        Assert.Contains("Titles must name the demonstrated feature", prompt, StringComparison.Ordinal);
        Assert.Contains("generic phrases are forbidden", prompt, StringComparison.Ordinal);
        Assert.Contains("one immediately recognisable hook", prompt, StringComparison.Ordinal);
        Assert.Contains("Output ONLY the prompt", prompt, StringComparison.Ordinal);

        var noText = VisualBriefWriter.BuildPrompt(request with { TextMayBeRendered = false, ReferenceKinds = [] });
        Assert.Contains("Text in the image: NOT allowed", noText, StringComparison.Ordinal);
        Assert.DoesNotContain("Attached references", noText, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-085: the writer is an art director — the piece's concrete moment, staged as a scene
    /// with a layout, named elements, a visible link between cause and answer, quoted labels on a
    /// spelling model, and a colour scheme built only from the brand palette, each colour given a role.
    /// </summary>
    [Fact]
    public void The_brief_writer_stages_the_pieces_concrete_moment_in_the_brand_palette()
    {
        var request = new VisualBriefRequest(
            SlotKind: "content-image", TargetWidth: 1600, TargetHeight: 840,
            Subject: "Conversational Analytics with Governed Queries",
            ContentDigest: "Ask \"What caused the revenue drop last quarter?\" and get a chart and an explanation.",
            CampaignBrief: null, CreativeDirection: null,
            BrandLook: "Brand palette: Primary #2D2A90, Accent #00B4D8, Background #EBEBF5.",
            Audience: null, ReferenceKinds: [], TextMayBeRendered: true);

        var prompt = VisualBriefWriter.BuildPrompt(request);

        Assert.Contains("single most concrete, showable moment", prompt, StringComparison.Ordinal);
        Assert.Contains("if the piece gives an example, use that example", prompt, StringComparison.Ordinal);
        Assert.Contains("split-screen left/right", prompt, StringComparison.Ordinal);
        Assert.Contains("visible connection", prompt, StringComparison.Ordinal);
        Assert.Contains("ONLY from the brand palette", prompt, StringComparison.Ordinal);
        Assert.Contains("Assign each brand colour a role", prompt, StringComparison.Ordinal);
        Assert.Contains("Primary #2D2A90", prompt, StringComparison.Ordinal);
        Assert.Contains("ALLOWED for the scene's own labels", prompt, StringComparison.Ordinal);
        Assert.Contains("At most six strings", prompt, StringComparison.Ordinal);
        Assert.Contains("never invented alternatives", prompt, StringComparison.Ordinal);
        Assert.Contains("never substitutes of your own", prompt, StringComparison.Ordinal);
        Assert.Contains("the scene's answer element states it", prompt, StringComparison.Ordinal);
        Assert.Contains("not an atmospheric backdrop", prompt, StringComparison.Ordinal);
        Assert.Contains("never present a statistic as a real claim", prompt, StringComparison.Ordinal);

        var composited = VisualBriefWriter.BuildPrompt(request with { HeadlineWillBeComposited = true });
        Assert.Contains("Do NOT paint a headline or page title", composited, StringComparison.Ordinal);

        var cannotSpell = VisualBriefWriter.BuildPrompt(request with { TextMayBeRendered = false });
        Assert.Contains("interface elements carry shapes, bars and lines instead of readable text", cannotSpell, StringComparison.Ordinal);
        Assert.DoesNotContain("ALLOWED for the scene's own labels", cannotSpell, StringComparison.Ordinal);

        var noBrand = VisualBriefWriter.BuildPrompt(request with { BrandLook = null });
        Assert.Contains("No brand look is set", noBrand, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gpt-image-2.5-sunburst", true)]
    [InlineData("gemini-3-pro-image", true)]
    [InlineData("gemini-2.5-flash-image", false)]
    [InlineData("MAI-Image-2.5-Pro", false)]
    public void Gemini_3_image_models_spell_like_the_gpt_image_family(string model, bool spells) =>
        Assert.Equal(spells, Castmill.Api.Services.Ai.ImageModelCapabilities.RendersText(model));

    [Fact]
    public void A_youtube_thumbnail_receives_the_brands_authoritative_youtube_template()
    {
        const string template = "Use a topic-specific title with a concrete, transcript-supported payoff.";
        var brand = new BrandContext(
            null, null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["youtube"] = template },
            null);

        var selected = ImagePromptBuilder.TemplateFor(
            new Castmill.Core.ImageSlot { Kind = "youtube-thumbnail", State = "Empty" },
            new Castmill.Core.Artifact { Kind = "youtube", Title = "Grid accessibility", ContentJson = "{}" },
            brand);

        Assert.Equal(template, selected);
    }

    [Fact]
    public void A_supporting_figure_fills_the_frame_while_a_headline_slot_reserves_space()
    {
        var figure = new VisualBriefRequest("content-image", 1280, 720, "Grid a11y", null, null, null, null, null, ["product"], false);
        var prompt = VisualBriefWriter.BuildPrompt(figure);
        Assert.Contains("fill the whole frame with the scene", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("leave one calm, clear area for it", prompt, StringComparison.Ordinal);
        Assert.Contains("product screenshot may be reproduced faithfully, including the text already on that screen", prompt, StringComparison.Ordinal);
        Assert.Contains("never a wireframe", prompt, StringComparison.Ordinal);

        var hero = VisualBriefWriter.BuildPrompt(figure with { SlotKind = "youtube-thumbnail", HeadlineWillBeComposited = true, ReferenceKinds = [] });
        Assert.Contains("leave one calm, clear area for it", hero, StringComparison.Ordinal);
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
