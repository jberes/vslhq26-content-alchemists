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

    /// <summary>Clients have said "clips" since F5; the generator's kind is "clip-suggestions".</summary>
    public static string Normalize(string kind) =>
        kind.Equals("clips", StringComparison.OrdinalIgnoreCase) ? "clip-suggestions" : kind;

    public static GeneratorSpec? Find(string kind) =>
        FanOut.FirstOrDefault(g => g.Kind.Equals(Normalize(kind), StringComparison.OrdinalIgnoreCase));

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
            $$"""
            Suggest 3-6 vertical short-form clips (YouTube Shorts, Reels, TikTok) from the transcript.
            Use segment timings for in/out points; points MUST fall inside the transcript's time range.
            Each clip MUST run between {{MinClipSeconds}} and {{MaxClipSeconds}} seconds — that is the
            short-form window, and a clip outside it will not be published as-is.
            Pick moments that stand alone without setup: a concrete claim, a number, a story beat,
            a contrarian take. Do not pick a moment that starts mid-sentence.
            For each clip also write the copy its upload form needs:
              "clipTitle": under 100 characters, no clickbait, states the payoff.
              "description": 1-2 sentences.
              "hashtags": 2-5 tags, no leading '#'.
            JSON schema: { "title": string, "clips": [ { "inSeconds": number, "outSeconds": number, "hook": string, "clipTitle": string, "description": string, "hashtags": string[], "platformFit": string[] } ], "citations": string[] }
            """,
            ValidateClips));

        specs.Add(new GeneratorSpec(
            "seo-brief",
            """
            Produce an SEO brief for this content:
            1. "summary": a ~150-word summary of what the content covers and who it serves.
            2. "focusKeywords": 5-10 search phrases this content could realistically rank for.
               Mix them: 2-3 short "head" terms people actually type (2-3 words, e.g.
               "content repurposing") plus specific mid/long-tail phrases.
            3. "youtubeTitles": exactly 3 alternative SEO-friendly YouTube titles for A/B testing —
               each under 100 characters, distinct angles (curiosity, benefit, keyword-led).
            JSON schema: { "title": string, "summary": string, "focusKeywords": string[], "youtubeTitles": string[], "citations": string[] }
            """,
            ValidateSeoBrief));

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

    /// <summary>The short-form window every platform shares. Enforced as a warning, not a
    /// fatal error: an over-long suggestion is still a usable moment a human can trim, and
    /// sinking the whole run over one clip would cost a full fan-out.</summary>
    internal const int MinClipSeconds = 15;
    internal const int MaxClipSeconds = 60;

    private static ValidationOutcome ValidateClips(JsonElement json, TranscriptContent transcript)
    {
        var common = ValidateCommon(json, transcript, requireArray: "clips", minItems: 1);
        if (!common.Passed)
        {
            return common;
        }
        var maxEnd = transcript.Segments.Max(s => s.EndSeconds);
        var warnings = new List<string>(common.Warnings);
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

            var length = end - start;
            if (length < MinClipSeconds || length > MaxClipSeconds)
            {
                warnings.Add(
                    $"Clip [{start:F0}s–{end:F0}s] runs {length:F0}s, outside the " +
                    $"{MinClipSeconds}–{MaxClipSeconds}s short-form window — trim it before publishing.");
            }

            if (clip.TryGetProperty("clipTitle", out var title)
                && title.ValueKind == JsonValueKind.String
                && title.GetString() is { Length: > 100 })
            {
                warnings.Add("A clip title is over YouTube's 100-character limit and will be truncated.");
            }
        }
        return new ValidationOutcome(true, warnings);
    }

    private static ValidationOutcome ValidateSeoBrief(JsonElement json, TranscriptContent transcript)
    {
        var common = ValidateCommon(json, transcript, requireString: "summary", requireArray: "focusKeywords", minItems: 3);
        if (!common.Passed)
        {
            return common;
        }
        if (!json.TryGetProperty("youtubeTitles", out var titles) || titles.ValueKind != JsonValueKind.Array
            || titles.GetArrayLength() != 3)
        {
            return new ValidationOutcome(false, [], "Exactly 3 youtubeTitles are required for A/B testing.");
        }
        foreach (var title in titles.EnumerateArray())
        {
            var text = title.GetString() ?? "";
            if (text.Length is 0 or > 100)
            {
                // 100 chars is YouTube's hard title limit.
                return new ValidationOutcome(false, [], $"YouTube title must be 1-100 chars; got {text.Length}.");
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
