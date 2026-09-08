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
    bool HeadlineWillBeComposited = false);

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

    /// <summary>The system brief, adapted from the owner's tested prompt-writing instructions.</summary>
    internal static string BuildPrompt(VisualBriefRequest r)
    {
        var text = new StringBuilder();
        text.AppendLine("""
            You turn a content brief into a clear, effective image-generation prompt.

            Treat the brief and the supplied references as the source of truth. Preserve
            explicit requirements. Where details are missing, make coherent creative choices
            appropriate to the image's purpose and audience.

            First determine:
            - What kind of image is needed and where it will be used.
            - The main subject or message — ONE hook, not a summary of everything.
            - What viewers should notice first.
            - Which supplied details are essential.

            Then write a self-contained image-generation prompt that describes:
            - The subject and its specific visual characteristics.
            - The composition, framing and visual hierarchy, with approximate proportions.
            - One appropriate style or medium.
            - Lighting, colours and materials, using the brand palette as it would actually be used.
            - Exact visible text in quotation marks ONLY where this brief allows text.
            - The aspect ratio.
            - Constraints and exclusions.

            Principles:
            - Translate abstract goals into visible design decisions.
            - Choose one coherent visual direction; keep supporting elements subordinate to the focal point.
            - Be specific without overloading the composition. Concrete descriptions, never strings
              of adjectives such as "stunning", "premium" or "beautiful".
            - Include only details relevant to this asset type. Do not turn the source material
              into visible text or into a list of features.
            - Do not invent factual claims, endorsements, statistics or brand requirements.
            - A product interface may be illustrated as a clean, simplified UI unless an exact
              screenshot reference is attached — then say to reproduce it faithfully.
            - No people unless a face reference is attached. No stock-photo look, no platform
              logos, no watermark.
            - For reference-based work, say what must be preserved and what may change.
            - The result must read as a finished, rich image at thumbnail size: real depth,
              lighting, materials and contrast, with a clear light-versus-dark structure. Never
              a flat wireframe, never grey placeholder bars standing in for content, never a
              diagram of empty boxes and lines, never a large empty white field. A dark or
              richly toned backdrop with one strong light source usually beats flat white unless
              the brand look demands light.
            - A product screenshot reference is real UI: describe it as the sharp, faithful
              screen it is, with its own on-screen text intact, framed with depth (a tilted
              device, a floating panel with a shadow, a glow behind it) — not redrawn as an
              abstract grid.

            Output ONLY the final image-generation prompt. No preamble, no headings, no notes.
            """);

        text.AppendLine("ASSET");
        text.Append("- Type: ").Append(AssetLabel(r.SlotKind)).Append(" · ").Append(r.TargetWidth).Append('×').Append(r.TargetHeight)
            .Append(" · aspect ").AppendLine(ImageAspect.Describe(r.TargetWidth, r.TargetHeight));
        text.AppendLine(AssetGuidance(r.SlotKind));
        text.Append("- Text in the image: ").AppendLine(r.TextMayBeRendered
            ? "ALLOWED. Choose a short headline of at most six words in two or three lines and give it in quotation marks, "
              + "plus at most one small label. Big, high contrast, readable at 320 pixels wide."
            : r.HeadlineWillBeComposited
                ? "NOT allowed. No NEW letters, words, numbers or logos; Castmill composites the headline afterwards. "
                  + "Leave one calm, clear area where that headline will sit, and fill the rest of the frame."
                : "NOT allowed. No NEW letters, words, numbers or logos. Nothing is composited later, so fill the "
                  + "whole frame with the subject — do not reserve empty areas for text.");
        if (r.ReferenceKinds.Contains("product", StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine("- The attached product screenshot may be reproduced faithfully, including the text already on that screen; only invented text is forbidden.");
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
        text.AppendLine("CONTENT BRIEF");
        text.Append("Subject: ").AppendLine(r.Subject);
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
        if (!string.IsNullOrWhiteSpace(r.BrandLook))
        {
            text.AppendLine("Brand look:");
            text.AppendLine(r.BrandLook.Trim());
        }
        return text.ToString();
    }

    private static string AssetLabel(string kind) => kind switch
    {
        "youtube-thumbnail" => "YouTube thumbnail",
        "blog-header" => "blog header image",
        "social-card" => "social card",
        var k when k.StartsWith("blog-inline-", StringComparison.Ordinal) => "in-article figure",
        _ => "supporting image",
    };

    private static string AssetGuidance(string kind) => kind switch
    {
        "youtube-thumbnail" =>
            "- This is judged at 320 pixels wide against other thumbnails: one immediately recognisable hook, "
            + "strong contrast, a single focal subject, one simplified supporting element at most. Keep the "
            + "bottom-right corner clear for the duration badge.",
        "blog-header" =>
            "- Wide and atmospheric, read at full page width. Subject centre-right, calm negative space on the "
            + "left for the page title. Editorial, not clip-art.",
        "social-card" =>
            "- Poster-like: one idea, legible in a phone feed without zooming.",
        var k when k.StartsWith("blog-inline-", StringComparison.Ordinal) =>
            "- Documentary and specific to the section it illustrates; no decoration for its own sake.",
        _ => "- A supporting image that adds information the text cannot.",
    };

    private static string Unfence(string text) =>
        text.StartsWith("```", StringComparison.Ordinal) && text.IndexOf('\n') is var nl && nl > 0 && text.LastIndexOf("```", StringComparison.Ordinal) is var last && last > nl
            ? text[(nl + 1)..last].Trim()
            : text;
}
