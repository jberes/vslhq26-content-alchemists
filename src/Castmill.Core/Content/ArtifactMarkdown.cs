using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Castmill.Core.Content;

/// <summary>
/// Reads an artifact's payload without caring which shape it is in.
///
/// Hand-authored artifacts keep their fields at the top level; the AI orchestrator wraps
/// generator output as <c>{ "content": {…}, "validation": {…} }</c>. Both shapes are live in
/// the database, and the client's <c>ArtifactContent.FindMarkdownHost</c> learned that the
/// hard way — the wrapper shape was a real bug, not a hypothetical. This lives in Core so the
/// server-side consumers (export, publishing) share the one implementation rather than each
/// re-deriving it and re-finding the bug.
/// </summary>
public static class ArtifactMarkdown
{
    /// <summary>The payload object, unwrapped from the orchestrator's envelope if present.</summary>
    public static JsonElement? Content(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(contentJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return root.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner.Clone()
                : root.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The markdown body, or null for a structured artifact that has none.</summary>
    public static string? Body(string? contentJson) =>
        Content(contentJson) is { } payload
        && payload.TryGetProperty("markdown", out var markdown)
        && markdown.ValueKind == JsonValueKind.String
            ? markdown.GetString()
            : null;

    public static string? MetaDescription(string? contentJson) =>
        String(contentJson, "metaDescription");

    public static string? Title(string? contentJson) => String(contentJson, "title");

    /// <summary>Citations recorded on the payload — the provenance trail (G5).</summary>
    public static IReadOnlyList<string> Citations(string? contentJson)
    {
        if (Content(contentJson) is not { } payload
            || !payload.TryGetProperty("citations", out var citations)
            || citations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. citations.EnumerateArray()
            .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)];
    }

    /// <summary>
    /// A structured artifact rendered as readable markdown for export. Deliberately a plain
    /// projection rather than a schema-aware template per kind: an export that shows a social
    /// post's text and hashtags is useful, and one that silently omits a field it did not
    /// recognise is worse than one that shows raw JSON.
    /// </summary>
    public static string ForExport(string kind, string? title, string? contentJson)
    {
        if (Body(contentJson) is { Length: > 0 } markdown)
        {
            return markdown;
        }

        if (Content(contentJson) is not { } payload)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"# {title}").AppendLine();
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (property.NameEquals("title") || property.NameEquals("citations"))
            {
                continue;
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"## {Humanize(property.Name)}").AppendLine();
            text.AppendLine(Render(property.Value)).AppendLine();
        }

        return text.ToString().TrimEnd() + "\n";
    }

    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.Object
                ? string.Join("  \n", item.EnumerateObject()
                    .Select(p => $"**{Humanize(p.Name)}:** {Render(p.Value)}"))
                : $"- {Render(item)}")),
        JsonValueKind.Object => string.Join("  \n", value.EnumerateObject()
            .Select(p => $"**{Humanize(p.Name)}:** {Render(p.Value)}")),
        _ => string.Empty,
    };

    /// <summary>camelCase field name → sentence-cased heading.</summary>
    internal static string Humanize(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && text.Length > 0)
            {
                text.Append(' ');
            }
            text.Append(text.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return text.ToString();
    }

    private static string? String(string? contentJson, string property) =>
        Content(contentJson) is { } payload
        && payload.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
