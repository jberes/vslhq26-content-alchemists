using System.Text.Json;
using Castmill.Core.Ai;

namespace Castmill.Api.Services.Ai;

public static class TranscriptService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Splits pasted plain text into sentence-ish segments with stable ids
    /// (S1, S2, …). Pasted text has no timing, so timestamps are estimated at
    /// a nominal reading pace purely to keep the schema uniform.
    /// </summary>
    public static TranscriptContent FromPlainText(string text, string? source)
    {
        const double secondsPerWord = 0.4;
        var segments = new List<TranscriptSegment>();
        var cursor = 0.0;
        var index = 1;

        foreach (var raw in SplitSentences(text))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0)
            {
                continue;
            }
            var words = sentence.Count(char.IsWhiteSpace) + 1;
            var duration = Math.Max(1.0, words * secondsPerWord);
            segments.Add(new TranscriptSegment($"S{index}", Math.Round(cursor, 2),
                Math.Round(cursor + duration, 2), null, sentence));
            cursor += duration;
            index++;
        }
        return new TranscriptContent(source ?? "pasted-text", segments);
    }

    public static TranscriptContent? Parse(string contentJson)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<TranscriptContent>(contentJson, Json);
            return parsed is { Segments.Count: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Transcript rendered for prompts: each segment prefixed with its citation id.</summary>
    public static string ToPromptText(TranscriptContent transcript)
    {
        // Timings ride along when the transcript has real ones — the clip generator's
        // in/out points and show-notes chapters are computed from them, and a model
        // asked for timings it was never shown correctly returns nothing. Pasted text
        // is segmented with zeroed times, which would only mislead.
        var timed = transcript.Segments.Any(s => s.EndSeconds > 0);
        return string.Join("\n", transcript.Segments.Select(s =>
            $"[{s.Id}{(timed ? FormattableString.Invariant($" {s.StartSeconds:0.0}s-{s.EndSeconds:0.0}s") : "")}]" +
            $"{(s.Speaker is null ? "" : $" {s.Speaker}:")} {s.Text}"));
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' or '\n' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
