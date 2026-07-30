using System.Text.Json;
using System.Text.Json.Nodes;

namespace Castmill.UI.Editor;

/// <summary>
/// Artifacts persist a typed JSON payload (backend ADR-003), and the markdown-bearing kinds
/// keep their prose in a <c>markdown</c> property alongside citations, meta description and
/// whatever else the generator produced.
///
/// The editor only owns the markdown. Everything else in that payload — citations especially,
/// which are the provenance backbone — must survive an edit untouched, so writes patch the
/// one property rather than replacing the document. Getting this wrong would silently drop
/// the provenance data that Phase F5's threads depend on.
/// </summary>
public static class ArtifactContent
{
    private const string MarkdownProperty = "markdown";

    /// <summary>
    /// Where the markdown lives. Hand-authored payloads keep it top-level; the AI
    /// orchestrator wraps generator output as {"content": {…markdown…}, "validation": …}.
    /// Rendering raw JSON at the user because of a wrapper object is exactly the failure
    /// this indirection prevents (found live, 2026-07-29).
    /// </summary>
    private static JsonObject? FindMarkdownHost(JsonObject obj)
    {
        if (obj.ContainsKey(MarkdownProperty))
        {
            return obj;
        }

        return obj.TryGetPropertyValue("content", out var content)
            && content is JsonObject nested
            && nested.ContainsKey(MarkdownProperty)
            ? nested
            : null;
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Pulls the editable markdown out of an artifact payload. Falls back to the raw string
    /// when the payload is not JSON or has no markdown property, so an unexpected shape is
    /// still editable rather than showing an empty page.
    /// </summary>
    public static string ToMarkdown(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(contentJson);

            if (node is JsonObject obj)
            {
                if (FindMarkdownHost(obj) is { } host
                    && host.TryGetPropertyValue(MarkdownProperty, out var markdown) && markdown is not null)
                {
                    return markdown.GetValue<string>();
                }

                // Some kinds (clip suggestions, keyword plans) are structured data with no
                // prose. Showing the JSON is more useful than showing nothing, and it is
                // still round-trip safe because it is written back as-is.
                return obj.ToJsonString(new JsonSerializerOptions(Options) { WriteIndented = true });
            }

            return contentJson;
        }
        catch (JsonException)
        {
            return contentJson;
        }
    }

    /// <summary>
    /// Writes edited markdown back into the payload, preserving every other property.
    /// </summary>
    public static string FromMarkdown(string? originalJson, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(originalJson) ? "{}" : originalJson) is JsonObject obj
                && FindMarkdownHost(obj) is { } host)
            {
                // Patch in place: citations, validation results and the rest of the payload
                // must survive an edit untouched, wherever the generator nested them.
                host[MarkdownProperty] = markdown;
                return obj.ToJsonString(Options);
            }
        }
        catch (JsonException)
        {
            // Fall through: an unparseable payload becomes a well-formed one.
        }

        // No markdown property to patch — either a fresh artifact or a structured kind whose
        // JSON the user edited directly. If the edited text is itself valid JSON, trust it;
        // otherwise wrap it as markdown.
        try
        {
            if (JsonNode.Parse(markdown) is JsonObject edited)
            {
                return edited.ToJsonString(Options);
            }
        }
        catch (JsonException)
        {
        }

        return new JsonObject { [MarkdownProperty] = markdown }.ToJsonString(Options);
    }
}

/// <summary>
/// Renders STRUCTURED artifact kinds (social posts, email sequences, landing pages…) as
/// readable markdown. These payloads have no single markdown body — they are typed fields
/// with server-side validators — so Focus shows them formatted but read-only: editing a
/// rendered projection would silently drop the structure the validators check.
/// </summary>
public static class StructuredContent
{
    /// <summary>True when the kind is structured — rendered read-only in Focus.</summary>
    public static bool IsStructured(string kind) =>
        kind.StartsWith("social-", StringComparison.Ordinal)
        || kind is "email-sequence" or "newsletter" or "landing-page" or "show-notes"
                or "seo-brief" or "seo-keyword-plan" or "keyword-plan" or "clips";

    /// <summary>Formats a structured payload as display markdown. Falls back to pretty JSON.</summary>
    public static string ToDisplayMarkdown(string kind, string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(contentJson);
            var root = doc.RootElement;

            // The orchestrator wraps generator output in {"content": …}.
            var c = root.TryGetProperty("content", out var nested) ? nested : root;

            var md = new System.Text.StringBuilder();

            if (kind.StartsWith("social-", StringComparison.Ordinal))
            {
                md.AppendLine(Str(c, "text"));
                if (c.TryGetProperty("hashtags", out var tags) && tags.GetArrayLength() > 0)
                {
                    md.AppendLine();
                    md.AppendLine(string.Join(' ', tags.EnumerateArray()
                        .Select(t => t.GetString())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t!.StartsWith('#') ? t : "#" + t)));
                }
            }
            else if (kind == "email-sequence" && c.TryGetProperty("emails", out var emails))
            {
                var i = 0;
                foreach (var email in emails.EnumerateArray())
                {
                    i++;
                    md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"## Email {i}: {Str(email, "subject")}");
                    md.AppendLine();
                    var preview = Str(email, "preview");
                    if (preview.Length > 0)
                    {
                        md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"*Inbox preview: {preview}*");
                        md.AppendLine();
                    }

                    md.AppendLine(Str(email, "bodyMarkdown"));
                    md.AppendLine();
                }
            }
            else if (kind == "newsletter")
            {
                md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"**Subject:** {Str(c, "subject")}");
                md.AppendLine();
                md.AppendLine(Str(c, "bodyMarkdown"));
            }
            else if (kind == "landing-page")
            {
                md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"# {Str(c, "headline")}");
                md.AppendLine();
                md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"**{Str(c, "subheadline")}**");
                md.AppendLine();
                if (c.TryGetProperty("sectionsMarkdown", out var sections))
                {
                    foreach (var section in sections.EnumerateArray())
                    {
                        md.AppendLine(section.GetString());
                        md.AppendLine();
                    }
                }

                md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"> **Call to action:** {Str(c, "cta")}");
            }
            else if (kind == "show-notes")
            {
                md.AppendLine(Str(c, "summaryMarkdown"));
                if (c.TryGetProperty("chapters", out var chapters) && chapters.GetArrayLength() > 0)
                {
                    md.AppendLine();
                    md.AppendLine("## Chapters");
                    md.AppendLine();
                    foreach (var ch in chapters.EnumerateArray())
                    {
                        var t = TimeSpan.FromSeconds(ch.TryGetProperty("startSeconds", out var s) ? s.GetDouble() : 0);
                        md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- **{(int)t.TotalMinutes}:{t.Seconds:00}** {Str(ch, "title")}");
                    }
                }
            }
            else if (kind == "seo-brief")
            {
                md.AppendLine(Str(c, "summary"));
                md.AppendLine();
                md.AppendLine("## Focus keywords");
                md.AppendLine();
                AppendList(md, c, "focusKeywords", "- ");
                md.AppendLine();
                md.AppendLine("## A/B YouTube titles");
                md.AppendLine();
                AppendList(md, c, "youtubeTitles", "1. ");
            }
            else if (kind is "seo-keyword-plan" or "keyword-plan")
            {
                md.AppendLine(Str(c, "summary"));
                md.AppendLine();
                md.AppendLine("*The full ranked table lives on the SEO analysis tab.*");
            }
            else
            {
                return contentJson;
            }

            return md.ToString().TrimEnd() + "\n";
        }
        catch (System.Text.Json.JsonException)
        {
            return contentJson;
        }
    }

    private static string Str(System.Text.Json.JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static void AppendList(
        System.Text.StringBuilder md, System.Text.Json.JsonElement element, string property, string bullet)
    {
        if (!element.TryGetProperty(property, out var list))
        {
            return;
        }

        foreach (var item in list.EnumerateArray())
        {
            md.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{bullet}{item.GetString()}");
        }
    }
}
