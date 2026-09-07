using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Castmill.Api.Services.Images;
using Castmill.Core;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// The ONE place an image prompt is assembled (ADR-055). Before this, six blocks were
/// appended in sequence — auto brief with 5,000 chars of raw JSON, brand style, a keyword
/// hint, reference instructions, "middle 76%" slot guardrails, "Text rendering rules" — and
/// they contradicted each other ("prefer keyword wording in text" vs "do not render any new
/// text"; 76% vs 60% safe zones). The model illustrated the noise.
///
/// The composer produces a short brief: what the image is for, what the piece says, the
/// creative direction, the brand's look, whether references are attached, and the
/// producer's adjustment — in that order, with the adjustment LAST so it is the most recent
/// instruction. It says nothing about margins or text: <see cref="ImagePromptRules"/> is
/// the single rules block and the renderer appends it, sized from the provider's real frame.
/// </summary>
public static class ImagePromptComposer
{
    /// <summary>Compose for a fresh generation.</summary>
    public static string Compose(
        ImageSlot slot, Campaign campaign, Artifact? owner, BrandContext brand,
        string? steeringNote = null, IReadOnlyList<ImageReference>? references = null)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(brand);

        var manual = string.Equals(slot.PromptMode, "Manual", StringComparison.OrdinalIgnoreCase);
        var text = new StringBuilder();

        if (manual)
        {
            // Verbatim: the producer chose to own this prompt.
            text.AppendLine((slot.Prompt ?? string.Empty).Trim());
        }
        else
        {
            text.AppendLine(Brief(slot.Kind));
            text.Append("Subject: ").AppendLine(Clean(owner?.Title ?? campaign.Name));
            if (ContentDigest(owner?.ContentJson) is { Length: > 0 } digest)
            {
                text.Append("What the piece says: ").AppendLine(digest);
            }
            if (!string.IsNullOrWhiteSpace(campaign.Brief))
            {
                text.Append("Campaign brief: ").AppendLine(Trim(Clean(campaign.Brief), 600));
            }
            if (!string.IsNullOrWhiteSpace(slot.Prompt))
            {
                text.Append("Creative direction: ").AppendLine(slot.Prompt.Trim());
            }
            if (!string.IsNullOrWhiteSpace(brand.ImageStyleBlock))
            {
                text.AppendLine(brand.ImageStyleBlock.Trim());
            }
            text.AppendLine(
                "Rebuild the composition from this brief every time; do not preserve a person, "
                + "background or product that is not in the attached references.");
        }

        AppendReferenceNote(text, references);
        AppendAdjustment(text, steeringNote);
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Compose a steered take from an earlier take's stored prompt: the same brief plus the
    /// adjustment. Legacy prompts carried the old guardrail blocks; they are stripped so a
    /// steer from an old take does not resurrect contradictory rules.
    /// </summary>
    public static string Steer(string sourcePrompt, string? note, IReadOnlyList<ImageReference>? references = null)
    {
        var text = new StringBuilder(StripLegacyRules(sourcePrompt).TrimEnd()).AppendLine();
        if (references is { Count: > 0 } && !text.ToString().Contains("reference image", StringComparison.OrdinalIgnoreCase))
        {
            AppendReferenceNote(text, references);
        }
        AppendAdjustment(text, note);
        return text.ToString().TrimEnd();
    }

    /// <summary>What each slot kind is FOR — the model composes differently for a thumbnail
    /// judged at 320 px than for a header read at full width.</summary>
    internal static string Brief(string kind) => kind switch
    {
        "youtube-thumbnail" =>
            "Create a YouTube thumbnail: one bold focal subject, high contrast, instantly readable "
            + "at 320 pixels wide. Keep the lower third visually calm — a headline is composited there later.",
        "blog-header" =>
            "Create a blog header image: editorial photography, wide and atmospheric, the subject "
            + "centre-right with calm negative space on the left for the page's title.",
        var k when k.StartsWith("blog-inline-", StringComparison.Ordinal) =>
            "Create a supporting blog figure: documentary and specific to the section it illustrates, "
            + "no decoration for its own sake.",
        "social-card" =>
            "Create a social card: a single idea, poster-like, legible at feed size on a phone.",
        _ => "Create a supporting image for the piece described below.",
    };

    private static void AppendReferenceNote(StringBuilder text, IReadOnlyList<ImageReference>? references)
    {
        if (references is not { Count: > 0 })
        {
            return;
        }
        // Every image gets a number and a job (ADR-074). "3 reference images are attached" told
        // the model nothing about which was the backdrop and which was the presenter, so it
        // treated a headshot as a scene and a scene as decoration — the "it ignores my
        // references" report. Numbering matches the order the images are sent in.
        text.Append(references.Count).Append(" reference image").Append(references.Count == 1 ? " is" : "s are")
            .AppendLine(" attached, in this order. Use their pixels, not descriptions of them:");
        for (var i = 0; i < references.Count; i++)
        {
            text.Append("- Image ").Append(i + 1).Append(": ").AppendLine(RoleInstruction(references[i].Kind));
        }
        if (references.Any(r => r.Kind == "face"))
        {
            text.AppendLine("Any person shown must be the person in the face reference — same face, hair, skin tone, "
                + "glasses and build. Do not substitute a different person, and do not paint text or a badge over them.");
        }
    }

    /// <summary>What the model should DO with each attached image, by the kind the brand kit gave it.</summary>
    internal static string RoleInstruction(string? kind) => kind switch
    {
        "background" => "the BACKGROUND. Build the scene on this image: keep its setting, lighting, palette and depth, "
            + "and place the other elements into it. Do not replace it with an invented environment.",
        "face" => "the PRESENTER. Reproduce this exact person's likeness. Keep them recognisable at a glance.",
        "product" => "the PRODUCT INTERFACE. Reproduce this real screen faithfully — its layout, controls and data. "
            + "Never invent replacement UI, panels or fake rows.",
        "logo" => "the LOGO. Reproduce it exactly, unwarped, in a clear area; never redraw or restyle it.",
        _ => "a visual reference for style and subject. Match its look; do not copy text from it.",
    };

    private static void AppendAdjustment(StringBuilder text, string? steeringNote)
    {
        if (!string.IsNullOrWhiteSpace(steeringNote))
        {
            text.Append("Adjustment: ").AppendLine(steeringNote.Trim());
        }
    }

    private static readonly string[] LegacyBlockStarts =
    [
        "Final composition target",
        "Text rendering rules",
        "COMPOSITION REQUIREMENTS",
        "EXACT OUTPUT SAFETY",
    ];

    /// <summary>Removes the pre-ADR-055 guardrail blocks and the keyword-in-text hint from a stored prompt.</summary>
    internal static string StripLegacyRules(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }
        var cut = prompt.Length;
        foreach (var marker in LegacyBlockStarts)
        {
            var at = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0 && at < cut)
            {
                cut = at;
            }
        }
        var kept = prompt[..cut];
        kept = Regex.Replace(kept, @"If the image contains any text, prefer wording that uses[^\n]*\n?", string.Empty);
        kept = Regex.Replace(kept, @"Never render a list of keywords\.?\n?", string.Empty);
        return kept.TrimEnd();
    }

    private static readonly string[] DigestKeys =
        ["title", "headline", "hook", "summary", "description", "excerpt", "subtitle", "metaDescription"];

    /// <summary>
    /// The human-readable core of an artifact payload, capped for a prompt: titles, hooks,
    /// summaries and the headings and opening line of any markdown body — never field
    /// names, citation markers or raw JSON. Hand-authored payloads keep their fields at the
    /// top level; generated ones sit under <c>content</c>, so both shapes are unwrapped.
    /// </summary>
    public static string? ContentDigest(string? contentJson, int maxChars = 1800)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return null;
        }
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Trim(Clean(contentJson), maxChars);
        }
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("content", out var inner)
            && inner.ValueKind == JsonValueKind.Object)
        {
            root = inner;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Trim(Clean(root.ToString()), maxChars);
        }

        var lines = new List<string>();
        foreach (var key in DigestKeys)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } text)
                {
                    lines.Add(Clean(text));
                }
            }
        }
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "markdown" or "body" or "script" or "text"
                && property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { Length: > 0 } body)
            {
                var headings = body.Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.StartsWith('#'))
                    .Select(Clean)
                    .Where(line => line.Length > 0)
                    .Take(8)
                    .ToList();
                if (headings.Count > 0)
                {
                    lines.Add("Sections: " + string.Join(" · ", headings));
                }
                var opening = body.Split('\n')
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.Length > 40 && !line.StartsWith('#') && !line.StartsWith('!'));
                if (opening is not null)
                {
                    lines.Add(Trim(Clean(opening), 400));
                }
                break;
            }
        }
        if (lines.Count == 0)
        {
            return Trim(Clean(root.ToString()), maxChars);
        }
        return Trim(string.Join("\n", lines.Distinct(StringComparer.Ordinal)), maxChars);
    }

    /// <summary>Markdown emphasis, headings, links, images and [[cite:…]] markers — none of it should be painted.</summary>
    internal static string Clean(string text)
    {
        var stripped = Regex.Replace(text, @"\[\[[^\]]*\]\]|!\[[^\]]*\]\([^)]*\)", " ");
        stripped = Regex.Replace(stripped, @"\[([^\]]+)\]\([^)]*\)", "$1");
        stripped = Regex.Replace(stripped, @"[#*_`>]+", " ");
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    internal static string Trim(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
}
