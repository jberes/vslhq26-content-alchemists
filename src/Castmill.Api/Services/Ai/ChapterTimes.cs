using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Castmill.Core.Ai;

namespace Castmill.Api.Services.Ai;

/// <summary>
/// Snaps YouTube chapter times onto real transcript moments (ADR-063).
///
/// The prompt asks for chapters that "come from real transcript moments" and nothing enforced
/// it: <c>ValidateYoutube</c> only checked that there are three and that the first is 0:00. A
/// 2026-09-06 audit of a shipped package found the model had in fact rounded real segment
/// starts (every chapter within 0.7s of one), but that was the model being careful, not the
/// system being correct — and a chapter a few seconds late opens on the wrong sentence.
///
/// The description is rewritten alongside the array, because the description is what gets
/// pasted into YouTube; an array that disagreed with the published text would be worse than
/// leaving both alone.
/// </summary>
internal static partial class ChapterTimes
{
    [GeneratedRegex(@"^(?<time>(?:\d{1,2}:)?\d{1,2}:\d{2})(?<rest>\s+\S.*)$")]
    private static partial Regex ChapterLine { get; }

    public static JsonElement Apply(JsonElement json, TranscriptContent transcript)
    {
        if (transcript.Segments.Count == 0
            || JsonNode.Parse(json.GetRawText()) is not JsonObject root
            || root["chapters"] is not JsonArray chapters
            || chapters.Count == 0)
        {
            return json;
        }

        var starts = transcript.Segments.Select(s => s.StartSeconds).OrderBy(s => s).ToList();
        var snapped = new List<int>(chapters.Count);
        foreach (var chapter in chapters)
        {
            var written = chapter?["startSeconds"]?.GetValue<double>() ?? 0;
            // Floor the segment's own start: a chapter that opens a moment early keeps the
            // first words, one that opens late loses them.
            var nearest = (int)Math.Floor(starts.OrderBy(s => Math.Abs(s - written)).First());
            // The first chapter must be 0:00 or YouTube creates no chapters at all, and each
            // one has to advance or the list silently collapses.
            var value = snapped.Count == 0 ? 0 : Math.Max(nearest, snapped[^1] + 1);
            snapped.Add(value);
        }

        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i] is JsonObject chapter)
            {
                chapter["startSeconds"] = snapped[i];
            }
        }

        if (root["description"]?.GetValue<string>() is { Length: > 0 } description)
        {
            root["description"] = RewriteDescription(description, snapped);
        }

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Replaces the timestamp on each chapter line, in order, leaving the titles and every
    /// other line untouched. If the description does not carry exactly one line per chapter
    /// the text is left alone — a partial rewrite would publish times that match nothing.
    /// </summary>
    internal static string RewriteDescription(string description, IReadOnlyList<int> snapped)
    {
        var lines = description.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var indices = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (ChapterLine.IsMatch(lines[i].Trim()))
            {
                indices.Add(i);
            }
        }
        if (indices.Count != snapped.Count)
        {
            return description;
        }

        for (var i = 0; i < indices.Count; i++)
        {
            var match = ChapterLine.Match(lines[indices[i]].Trim());
            lines[indices[i]] = Format(snapped[i]) + match.Groups["rest"].Value;
        }
        return string.Join('\n', lines);
    }

    /// <summary>YouTube's own format: mm:ss under an hour, h:mm:ss beyond it.</summary>
    internal static string Format(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }
}
