using System.Text.Json;
using Castmill.Core.Ai;

namespace Castmill.Api.Services.Ai;

public sealed record ValidationOutcome(bool Passed, IReadOnlyList<string> Warnings, string? FatalError = null);

public sealed record GeneratorSpec(
    string Kind,
    string Instructions,
    Func<JsonElement, TranscriptContent, ValidationOutcome> Validate);

/// <summary>
/// The fan-out set (B5.3/B5.4). Every generator returns strict JSON with a
/// top-level "citations" array of transcript segment ids — provenance is a
/// schema requirement (G5), not a convention. Deterministic validators run
/// before anything persists.
/// </summary>
public static class Generators
{
    public const string CommonContract = """
        Respond with ONLY a JSON object — no markdown fences, no commentary.
        Every claim must trace to the source transcript. Include a top-level
        "citations" array containing the ids (e.g. "S12") of every transcript
        segment you drew from. Never cite a segment id that does not exist.
        """;

    private static readonly string[] SocialPlatforms = ["x", "linkedin", "facebook", "instagram", "threads", "bluesky"];

    public static IReadOnlyList<GeneratorSpec> FanOut { get; } = BuildFanOut();

    public static GeneratorSpec? Find(string kind) =>
        FanOut.FirstOrDefault(g => g.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

    private static List<GeneratorSpec> BuildFanOut()
    {
        var specs = new List<GeneratorSpec>();

        foreach (var platform in SocialPlatforms)
        {
            var cap = PlatformLimits.MaxChars[platform];
            specs.Add(new GeneratorSpec(
                $"social-{platform}",
                $$"""
                Write one {{platform}} post promoting the source content.
                Hard limit: {{cap}} characters for the "text" value — this is validated and over-limit posts are rejected.
                Match the platform's native voice. Include hashtags only where the platform culture expects them.
                JSON schema: { "title": string, "text": string, "hashtags": string[], "citations": string[] }
                """,
                (json, t) => ValidateSocial(json, t, cap)));
        }

        specs.Add(new GeneratorSpec(
            "email-sequence",
            """
            Write a 3-email nurture sequence based on the source content.
            JSON schema: { "title": string, "emails": [ { "subject": string, "preview": string, "bodyMarkdown": string } ], "citations": string[] }
            """,
            (json, t) => ValidateCommon(json, t, requireArray: "emails", minItems: 3)));

        specs.Add(new GeneratorSpec(
            "newsletter",
            """
            Write a newsletter edition based on the source content.
            JSON schema: { "title": string, "subject": string, "bodyMarkdown": string, "citations": string[] }
            """,
            (json, t) => ValidateCommon(json, t, requireString: "bodyMarkdown")));

        specs.Add(new GeneratorSpec(
            "landing-page",
            """
            Write landing page copy for the offer/topic in the source content.
            JSON schema: { "title": string, "headline": string, "subheadline": string, "sectionsMarkdown": string[], "cta": string, "citations": string[] }
            """,
            (json, t) => ValidateCommon(json, t, requireString: "headline")));

        specs.Add(new GeneratorSpec(
            "show-notes",
            """
            Write episode show notes with timestamped chapters from the source transcript.
            JSON schema: { "title": string, "summaryMarkdown": string, "chapters": [ { "startSeconds": number, "title": string } ], "citations": string[] }
            """,
            (json, t) => ValidateCommon(json, t, requireArray: "chapters", minItems: 1)));

        specs.Add(new GeneratorSpec(
            "clip-suggestions",
            """
            Suggest 3-6 short vertical clips from the transcript. Use segment timings for in/out points; points MUST fall inside the transcript's time range.
            JSON schema: { "title": string, "clips": [ { "inSeconds": number, "outSeconds": number, "hook": string, "platformFit": string[] } ], "citations": string[] }
            """,
            ValidateClips));

        specs.Add(new GeneratorSpec(
            "image-prompts",
            """
            Write image-generation prompts for this campaign: one blog hero image, one YouTube thumbnail (bold, readable at small size), and 2 supporting blog images.
            JSON schema: { "title": string, "images": [ { "slot": string, "prompt": string, "aspectRatio": string } ], "citations": string[] }
            "slot" is one of: "blog-hero", "youtube-thumbnail", "blog-inline-1", "blog-inline-2".
            """,
            (json, t) => ValidateCommon(json, t, requireArray: "images", minItems: 3)));

        return specs;
    }

    // ---- Validators ---------------------------------------------------------

    public static ValidationOutcome ValidateCitations(JsonElement json, TranscriptContent transcript)
    {
        if (!json.TryGetProperty("citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
        {
            return new ValidationOutcome(false, [], "Missing required 'citations' array (provenance contract).");
        }
        var ids = citations.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString()!)
            .ToList();
        if (ids.Count == 0)
        {
            return new ValidationOutcome(false, [], "At least one citation is required.");
        }
        var known = transcript.Segments.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = ids.Where(id => !known.Contains(id)).ToList();
        return unknown.Count > 0
            ? new ValidationOutcome(false, [], $"Citations reference unknown segments: {string.Join(", ", unknown)}.")
            : new ValidationOutcome(true, []);
    }

    private static ValidationOutcome ValidateCommon(
        JsonElement json, TranscriptContent transcript,
        string? requireString = null, string? requireArray = null, int minItems = 0)
    {
        var citations = ValidateCitations(json, transcript);
        if (!citations.Passed)
        {
            return citations;
        }
        if (requireString is not null &&
            (!json.TryGetProperty(requireString, out var s) || s.ValueKind != JsonValueKind.String || s.GetString()!.Length == 0))
        {
            return new ValidationOutcome(false, [], $"Missing required field '{requireString}'.");
        }
        if (requireArray is not null &&
            (!json.TryGetProperty(requireArray, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < minItems))
        {
            return new ValidationOutcome(false, [], $"Field '{requireArray}' must be an array with at least {minItems} items.");
        }
        return new ValidationOutcome(true, []);
    }

    private static ValidationOutcome ValidateSocial(JsonElement json, TranscriptContent transcript, int cap)
    {
        var common = ValidateCommon(json, transcript, requireString: "text");
        if (!common.Passed)
        {
            return common;
        }
        var text = json.GetProperty("text").GetString()!;
        return text.Length > cap
            ? new ValidationOutcome(false, [], $"Post is {text.Length} chars — over the {cap}-char hard cap.")
            : new ValidationOutcome(true, text.Length > cap * 0.95 ? [$"Post is within {cap - text.Length} chars of the cap."] : []);
    }

    private static ValidationOutcome ValidateClips(JsonElement json, TranscriptContent transcript)
    {
        var common = ValidateCommon(json, transcript, requireArray: "clips", minItems: 1);
        if (!common.Passed)
        {
            return common;
        }
        var maxEnd = transcript.Segments.Max(s => s.EndSeconds);
        foreach (var clip in json.GetProperty("clips").EnumerateArray())
        {
            if (!clip.TryGetProperty("inSeconds", out var inS) || !clip.TryGetProperty("outSeconds", out var outS))
            {
                return new ValidationOutcome(false, [], "Every clip needs inSeconds and outSeconds.");
            }
            var start = inS.GetDouble();
            var end = outS.GetDouble();
            if (start < 0 || end <= start || end > maxEnd + 1)
            {
                return new ValidationOutcome(false, [],
                    $"Clip [{start:F1}s–{end:F1}s] falls outside the source duration (0–{maxEnd:F1}s).");
            }
        }
        return new ValidationOutcome(true, []);
    }

    /// <summary>Blog validator (B5.2): word band + citations.</summary>
    public static ValidationOutcome ValidateBlog(JsonElement json, TranscriptContent transcript)
    {
        var common = ValidateCommon(json, transcript, requireString: "markdown");
        if (!common.Passed)
        {
            return common;
        }
        var words = json.GetProperty("markdown").GetString()!
            .Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        var warnings = new List<string>();
        if (words is < 800 or > 3200)
        {
            return new ValidationOutcome(false, [], $"Blog draft is {words} words — outside the 800–3200 review band.");
        }
        if (words is < 1500 or > 2500)
        {
            warnings.Add($"Blog draft is {words} words — target band is 1500–2500.");
        }
        return new ValidationOutcome(true, warnings);
    }
}
