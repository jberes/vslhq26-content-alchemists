using System.Diagnostics;
using System.Text;
using Castmill.Api.Services.Images;
using Castmill.Core;
using Microsoft.Extensions.AI;

namespace Castmill.Api.Services.Ai;

/// <summary>Everything the brief writer may know about the image it is briefing.</summary>
public sealed record VisualBriefRequest(
    string SlotKind,
    int TargetWidth,
    int TargetHeight,
    string Subject,
    string? ContentDigest,
    string? CampaignBrief,
    string? CreativeDirection,
    string? BrandLook,
    string? Audience,
    IReadOnlyList<string> ReferenceKinds,
    bool TextMayBeRendered,
    /// <summary>Castmill will composite a headline on this image after generation, so the brief
    /// must reserve calm space for it. False for supporting figures, which fill the frame.</summary>
    bool HeadlineWillBeComposited = false,
    /// <summary>The brand's authoritative content template for the owning artifact. Thumbnail
    /// title and visual directions in it must survive into image generation too.</summary>
    string? BrandContentTemplate = null);

public interface IVisualBriefWriter
{
    /// <summary>A self-contained image prompt, or null when the writer could not run — the
    /// caller falls back to the deterministic composer and the render still happens.</summary>
    Task<string?> WriteAsync(Guid userId, VisualBriefRequest request, CancellationToken ct);
}

/// <summary>
/// Stage 1 of image generation (ADR-075): a text model turns the piece into a visual brief
/// before any pixels are asked for. The deterministic composer concatenated a kind blurb, a
/// title, a 1,800-character content digest, the campaign brief and the brand block — a
/// description of the material, not a design. An image model given material and no design
/// makes a safe, generic picture, which is what every take looked like.
///
/// The writer makes the design decisions the composer could not: the one hook, the focal
/// point, the hierarchy, the palette as used, and — for a thumbnail on a model that spells —
/// the exact words. Its output is the prompt; the composer then appends only the reference
/// roles and any adjustment, and the renderer appends the frame rules.
/// </summary>
public sealed class VisualBriefWriter(
    IChatProviderRegistry chatProviders,
    IPromptLog promptLog,
    TimeProvider clock,
    ILogger<VisualBriefWriter> logger) : IVisualBriefWriter
{
    public async Task<string?> WriteAsync(Guid userId, VisualBriefRequest request, CancellationToken ct)
    {
        var prompt = BuildPrompt(request);
        var stopwatch = Stopwatch.StartNew();
        var responseText = string.Empty;
        var success = false;
        try
        {
            var client = await chatProviders.ResolveAsync(userId, "chat", ct);
            var response = await client.GetResponseAsync(prompt, cancellationToken: ct);
            responseText = response.Text.Trim();
            success = responseText.Length > 40;
            return success ? Unfence(responseText) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Visual brief writer failed for {Kind}; falling back to the composer", request.SlotKind);
            return null;
        }
        finally
        {
            promptLog.Record(new PromptLogEntry(
                clock.GetUtcNow(), userId, $"{request.SlotKind}-brief", "chat",
                prompt.Length <= 600 ? prompt : prompt[..600] + "…",
                responseText.Length <= 600 ? responseText : responseText[..600] + "…",
                success, stopwatch.ElapsedMilliseconds));
        }
    }

    /// <summary>
    /// The art-director instructions (ADR-085). Modelled on the anatomy of prompts that produce
    /// strong editorial images: the piece's one concrete moment, staged as a specific scene with
    /// an explicit layout, named elements and the relationship between them, exact short labels
    /// where the model can spell, a few subordinate icons, and a colour scheme built only from
    /// the brand palette. The previous version asked for "one hook" and forbade all text, which
    /// steered every take towards an abstract gradient with a glow.
    /// </summary>
    internal static string BuildPrompt(VisualBriefRequest r)
    {
        var text = new StringBuilder();
        text.AppendLine("""
            You are the art director for this content. Write ONE image-generation prompt for a
            finished, publication-quality image that shows what this piece is about at a glance.

            METHOD
            1. Find the piece's single most concrete, showable moment: a real question someone
               asks, a problem and its answer, a before and after, a workflow step, a result.
               Take it from the content itself; if the piece gives an example, use that example.
               Never settle for the topic in the abstract ("data", "AI", "collaboration").
               Where the piece names specifics — a question, a region, a metric, a cause, a number,
               a product — the scene uses exactly those, never substitutes of your own. When it
               gives an answer, cause or outcome, the scene's answer element states it (quoted,
               condensed to a short label) — an answer panel that only says "Explanation" wastes
               the image.
            2. Stage that moment as a specific scene. For software, data or product topics, show
               the product doing it: a detailed, realistic interface — for example a dashboard
               panel with a named chart type and the exact data point that matters highlighted, an
               assistant panel with the user's question, a result card with the answer, and a
               visible connection (a glowing line, an arrow, a highlight) linking cause to answer.
               For other topics, a concrete editorial scene with real objects, places and actions.
            3. Give the layout explicitly: split-screen left/right, a hero device or panel with
               supporting cards, before/after, foreground and background — with rough proportions
               and the order the eye travels (first, second, third).
            4. Name 3 to 6 elements and give each its specific visual characteristics. Supporting
               elements stay subordinate to the focal one.
            5. Optionally add up to three small minimalist icons for the piece's secondary themes
               (for example a shield for governance, a link for connected data), placed quietly in
               one corner area inside the safe area.
            6. Colour: build the whole colour scheme ONLY from the brand palette given below.
               Assign each brand colour a role — background, panels and surfaces, the focal accent,
               the one highlight the viewer must notice, lines and text — and name each colour in
               words with its hex once. Do not introduce other hues. With no brand palette, choose a
               restrained scheme that suits the subject.
            7. Finish: the aesthetic (clean and technical, editorial, cinematic …), lighting, depth
               and materials, so it reads as a rich finished image, never a wireframe, a diagram of
               empty boxes, grey placeholder bars or a large empty field.

            RULES
            - Concrete nouns and specifics, never strings of adjectives ("stunning", "premium").
            - Illustrative interface content may show plausible sample values that match the
              piece's own example; never present a statistic as a real claim about a named
              company, customer or result the piece does not state.
            - No people unless a face reference is attached. No stock-photo look, no platform
              logos, no watermark.
            - A product screenshot reference is real UI: describe it as the sharp, faithful screen
              it is, text intact, framed with depth (a tilted device, a floating panel with a
              shadow) — not redrawn as an abstract grid.
            - For reference-based work, say what must be preserved and what may change.

            OUTPUT
            One paragraph, 120–220 words, starting with the asset type (for example "A
            professional blog header image with a split-screen visual. On the left side, …").
            Output ONLY the prompt — no preamble, headings or notes.

            EXAMPLE OF THE LEVEL OF SPECIFICITY (structure only; never reuse its content):
            "A professional blog header image with a split-screen visual. On the left, a
            deployment pipeline board in a dark UI shows five stages with the fourth, 'Integration
            tests', flagged red. On the right, an incident card reads 'Rollback completed in 42s'
            beneath a small bar chart of deploy times. A glowing line links the red stage to the
            card. Three minimalist icons sit in the top-right corner: a shield, a clock and a
            branch. Deep navy #0B1F3A background, panels in slate, the red stage and the line in
            the brand highlight, everything else calm; crisp, technical, softly lit."
            """);

        text.AppendLine("ASSET");
        text.Append("- Type: ").Append(AssetLabel(r.SlotKind)).Append(" · ").Append(r.TargetWidth).Append('×').Append(r.TargetHeight)
            .Append(" · aspect ").AppendLine(ImageAspect.Describe(r.TargetWidth, r.TargetHeight));
        text.AppendLine(AssetGuidance(r.SlotKind));
        text.Append("- Text in the image: ").AppendLine(TextPolicy(r));
        if (r.ReferenceKinds.Contains("product", StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine("- The attached product screenshot may be reproduced faithfully, including the text already on that screen.");
        }
        if (r.ReferenceKinds.Count > 0)
        {
            text.Append("- Attached references, in order: ").AppendLine(string.Join(", ", r.ReferenceKinds));
        }
        if (!string.IsNullOrWhiteSpace(r.Audience))
        {
            text.Append("- Audience: ").AppendLine(r.Audience.Trim());
        }

        text.AppendLine();
        text.AppendLine("CONTENT");
        text.Append("Title: ").AppendLine(r.Subject);
        if (!string.IsNullOrWhiteSpace(r.ContentDigest))
        {
            text.Append("What the piece says: ").AppendLine(r.ContentDigest.Trim());
        }
        if (!string.IsNullOrWhiteSpace(r.CampaignBrief))
        {
            text.Append("Campaign: ").AppendLine(r.CampaignBrief.Trim());
        }
        if (!string.IsNullOrWhiteSpace(r.CreativeDirection))
        {
            text.Append("Producer's direction (honour it): ").AppendLine(r.CreativeDirection.Trim());
        }
        text.AppendLine();
        text.AppendLine("BRAND");
        text.AppendLine(string.IsNullOrWhiteSpace(r.BrandLook)
            ? "No brand look is set: choose a restrained palette that suits the subject."
            : r.BrandLook.Trim());
        if (!string.IsNullOrWhiteSpace(r.BrandContentTemplate))
        {
            text.AppendLine();
            text.AppendLine("AUTHORITATIVE BRAND CONTENT TEMPLATE");
            text.AppendLine("Apply every relevant thumbnail and title requirement below. It overrides conflicting generic advice:");
            text.AppendLine(r.BrandContentTemplate.Trim());
        }
        return text.ToString();
    }

    private static string TextPolicy(VisualBriefRequest r)
    {
        if (!r.TextMayBeRendered)
        {
            return r.HeadlineWillBeComposited
                ? "NOT allowed. No letters, words, numbers or logos anywhere — interface elements carry "
                  + "shapes, bars and lines instead of readable text. Castmill composites the headline "
                  + "afterwards: leave one calm, clear area for it and fill the rest of the frame."
                : "NOT allowed. No letters, words, numbers or logos anywhere — interface elements carry "
                  + "shapes, bars and lines instead of readable text. Nothing is composited later, so fill "
                  + "the whole frame with the scene.";
        }
        if (r.SlotKind == "youtube-thumbnail")
        {
            return "REQUIRED. Give ONE exact title in quotation marks, at most six words over two or three lines. "
                + "It must be compelling, concrete and specific to the actual subject, feature, problem or payoff in the content — never a generic label or vague hype. "
                + "Make it the dominant high-contrast element, fully readable at 320 pixels wide. Any interface in the scene may carry at most two short quoted labels.";
        }
        var labels = "ALLOWED for the scene's own labels: give every visible string exactly, in quotation marks — "
            + "a chart title, the question typed into the assistant, the answer's headline, a data label. "
            + "At most six strings, each at most twelve words, quoted from the content or directly stated by it "
            + "(its own question, names, regions, metrics and causes — never invented alternatives). "
            + "No other text, no brand names or logos you were not given.";
        return r.HeadlineWillBeComposited
            ? labels + " Do NOT paint a headline or page title: Castmill composites it afterwards in one calm area you leave clear."
            : labels + " Do not add a headline or page title; fill the frame with the scene.";
    }

    private static string AssetLabel(string kind) => kind switch
    {
        "youtube-thumbnail" => "YouTube thumbnail",
        "blog-header" => "blog header image",
        "social-card" => "social card",
        var k when k.StartsWith("blog-inline-", StringComparison.Ordinal) => "in-article figure",
        _ => "blog/content image",
    };

    private static string AssetGuidance(string kind) => kind switch
    {
        "youtube-thumbnail" =>
            "- Judged at 320 pixels wide against other thumbnails: one immediately recognisable hook, "
            + "strong contrast, a single focal subject, one simplified supporting element at most. Keep the "
            + "bottom-right corner clear for the duration badge. The title and focal visual must express the "
            + "same specific promise from this video's real topic; generic phrases are forbidden.",
        "social-card" =>
            "- Poster-like: one idea staged boldly, legible in a phone feed without zooming.",
        var k when k.StartsWith("blog-inline-", StringComparison.Ordinal) =>
            "- Documentary and specific to the section it illustrates; show the step or result that section describes.",
        _ =>
            "- A header-grade editorial image read at full page width: a rich, specific scene that fills the frame "
            + "inside the safe area — not an atmospheric backdrop.",
    };

    private static string Unfence(string text) =>
        text.StartsWith("```", StringComparison.Ordinal) && text.IndexOf('\n') is var nl && nl > 0 && text.LastIndexOf("```", StringComparison.Ordinal) is var last && last > nl
            ? text[(nl + 1)..last].Trim()
            : text;
}
