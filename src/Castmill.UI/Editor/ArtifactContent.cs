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
                if (obj.TryGetPropertyValue(MarkdownProperty, out var markdown) && markdown is not null)
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
                && obj.ContainsKey(MarkdownProperty))
            {
                obj[MarkdownProperty] = markdown;
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
