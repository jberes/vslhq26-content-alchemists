using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Removes inline segment markers — <c>[s03][s08][s09]</c> — from generated prose.
///
/// The prompt shows the transcript with bracketed segment ids so the model can ground what
/// it writes, and the contract asks for a top-level <c>citations</c> array. Models do both:
/// they fill the array AND sprinkle the markers through the body, so published copy came out
/// reading "…then add the required middleware. [s03][s08][s09][s17][s19][s21]".
///
/// Provenance is not lost by stripping them: it lives in the <c>citations</c> array, which is
/// validated against real segment ids and surfaced by the <c>CitationsJson</c> computed
/// column that draws the board's provenance threads. The markers were duplicating it in the
/// one place it must not appear — the copy someone publishes.
/// </summary>
internal static partial class CitationMarkers
{
    /// <summary>
    /// A run of one or more markers, plus any space in front of them. Deliberately narrow:
    /// only <c>[</c> + <c>s</c>/<c>S</c> + digits + <c>]</c>, so a markdown link
    /// <c>[text](url)</c>, a footnote <c>[1]</c> or an alert marker <c>[!NOTE]</c> is never
    /// touched.
    /// </summary>
    [GeneratedRegex(@"[ \t]*(?:\[[sS]\d{1,4}\])+")]
    private static partial Regex MarkerRun();

    /// <summary>Space left dangling before punctuation once a marker is removed.</summary>
    [GeneratedRegex(@"[ \t]+([.,;:!?])")]
    private static partial Regex SpaceBeforePunctuation();

    /// <summary>Fields that hold prose a human will read or publish.</summary>
    private static readonly HashSet<string> ProseFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "markdown", "text", "bodyMarkdown", "summary", "summaryMarkdown", "description",
        "metaDescription", "headline", "subheadline", "preview", "subject", "hook",
        "clipTitle", "title", "cta", "angle", "rationale",
    };

    /// <summary>
    /// Strips markers from every prose field in the payload, at any depth. The
    /// <c>citations</c> array is left completely alone — that is where provenance belongs.
    /// </summary>
    public static JsonElement Strip(JsonElement json)
    {
        if (JsonNode.Parse(json.GetRawText()) is not { } root)
        {
            return json;
        }

        Walk(root, insideCitations: false);

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void Walk(JsonNode node, bool insideCitations)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is null)
                    {
                        continue;
                    }

                    var isCitations = key.Equals("citations", StringComparison.OrdinalIgnoreCase);
                    if (child is JsonValue value && !isCitations && ProseFields.Contains(key)
                        && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Clean(text);
                    }
                    else
                    {
                        Walk(child, insideCitations || isCitations);
                    }
                }
                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Walk(child, insideCitations);
                    }
                }
                break;
        }
    }

    /// <summary>Public for tests and for the same pass on already-stored content.</summary>
    internal static string Clean(string text)
    {
        if (text.Length == 0 || !text.Contains('[', StringComparison.Ordinal))
        {
            return text;
        }

        var cleaned = MarkerRun().Replace(text, string.Empty);
        cleaned = SpaceBeforePunctuation().Replace(cleaned, "$1");

        // A marker run can leave a line that was nothing else behind as trailing whitespace.
        return string.Join('\n', cleaned.Split('\n').Select(line => line.TrimEnd()));
    }
}
