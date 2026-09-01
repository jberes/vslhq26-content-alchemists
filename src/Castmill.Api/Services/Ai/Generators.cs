using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Castmill.Core.Ai;

namespace Castmill.Api.Services.Ai;

public sealed record ValidationOutcome(bool Passed, IReadOnlyList<string> Warnings, string? FatalError = null);

public sealed record GeneratorSpec(
    string Kind,
    string Instructions,
    Func<JsonElement, GenerationEvidenceContext, ValidationOutcome> ValidateEvidence,
    /// <summary>
    /// Optional deterministic pass over the model's output BEFORE validation. Exists for the
    /// clip generator, which asks the model which segments a clip spans and then computes the
    /// timings itself rather than trusting numbers the model wrote.
    /// </summary>
    Func<JsonElement, TranscriptContent, JsonElement>? Transform = null)
{
    public ValidationOutcome Validate(JsonElement json, GenerationEvidenceContext evidence) =>
        ValidateEvidence(json, evidence);

    public ValidationOutcome Validate(JsonElement json, TranscriptContent transcript) =>
        ValidateEvidence(json, GenerationEvidenceContext.FromTranscript(transcript));
}

/// <summary>
/// The fan-out set (B5.3/B5.4). Every generator returns strict JSON with a
/// top-level "citations" array of approved evidence ids — provenance is a
/// schema requirement (G5), not a convention. Deterministic validators run
/// before anything persists.
/// </summary>
public static partial class Generators
{
    public const string EmailVideoPlaceholder = "[YOUTUBE_VIDEO_URL]";

    public const string CommonContract = """
        Respond with ONLY a JSON object — no markdown fences, no commentary.
        Every claim must trace to approved source evidence. Include a top-level
        "citations" array containing the exact qualified Citation ID values of every
        evidence block you drew from. Never invent or shorten a Citation ID.
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

        // YouTube is its own category, not a social post: the description is a long-form SEO
        // surface with its own conventions (front-loaded keywords, chapters, a link block),
        // and the title is A/B tested, so one title is not enough.
        specs.Add(new GeneratorSpec(
            "youtube",
            """
            Write the YouTube package for this video.
            JSON schema: {
              "title": string,
              "titleOptions": [
                { "slot": "A" | "B" | "C", "title": string,
                  "angle": "seo" | "curiosity" | "how-to" | "problem-solution" | "thought-leadership",
                  "score": number, "rationale": string }
              ],
              "description": string,
              "chapters": [ { "startSeconds": number, "title": string } ],
              "tags": [ string ],
              "suggestedPinnedComment": string,
              "audit": {
                "hookWithin125": boolean,
                "hashtagsHoisted": boolean,
                "chapterKeywordsPresent": boolean,
                "warnings": [ string ]
              },
              "citations": string[]
            }

            Produce ONE package — the single best description you can write. The only thing
            there are alternatives of is the title.

            "title" is the recommended title. "titleOptions" holds EXACTLY three A/B/C titles,
            each using a distinct angle from the supported taxonomy (SEO, curiosity, how-to,
            problem-solution, thought-leadership), with a 0-100
            predicted performance score and a short rationale. Every title must:
            - be under 60 characters, or search truncates it;
            - carry the primary keyword in the first half, where it is weighted and always
              visible;
            - read as a phrase a person would actually type or ask.

            Answer-engine optimisation matters as much as search here: the description is what
            an AI assistant quotes when asked about this topic. So state the answer plainly
            somewhere in the first two paragraphs — a direct, self-contained sentence that makes
            sense lifted out of context, with no "in this video" preamble. Prefer the concrete
            noun over the clever one.

            "description":
            - The first 2 lines are the only ones shown before "...more" — put the payoff and
              the primary keyword there, and never open with a greeting.
            - Then 2-4 short paragraphs, written for a reader, that naturally carry the terms
              someone would search for. No keyword stuffing.
            - Then a "Chapters:" section listing each chapter as "M:SS Title", starting at 0:00
              (YouTube only creates chapters when the first is 0:00 and there are 3 or more).
            - Then leave the exact line {{LINKS}} on its own where the link block belongs. Do
              not invent URLs; that placeholder is replaced with the real ones.

            "chapters" must be in ascending order, start at 0, and come from real transcript
            moments. Put a natural target keyword in every chapter title. "tags" is 8-15
            specific search terms, no hashes. If hashtags are useful, place no more than three
            on the final line of the description — never in its opening hook.

            "suggestedPinnedComment" must refer to one concrete transcript moment, add useful
            context rather than repeat the description, and end with an open question.
            """,
            ValidateYoutube));

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
            $$"""
            Write a 3-email nurture sequence based on the source content.
            JSON schema: { "title": string, "emails": [ { "subject": string, "preview": string, "bodyMarkdown": string } ], "citations": string[] }
            Where the final YouTube video URL belongs, write the exact literal
            {{EmailVideoPlaceholder}} for replacement in the user's email service provider.
            Include it in at least one email body. Never invent or write a YouTube URL.
            """,
            ValidateEmailSequence));

        specs.Add(new GeneratorSpec(
            "newsletter",
            $$"""
            Write a newsletter edition based on the source content.
            JSON schema: { "title": string, "subject": string, "bodyMarkdown": string, "citations": string[] }
            Where the final YouTube video URL belongs, write the exact literal
            {{EmailVideoPlaceholder}} for replacement in the user's email service provider.
            Never invent or write a YouTube URL.
            """,
            ValidateNewsletter));

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

            DO NOT WRITE TIMESTAMPS. Identify each clip by the transcript SEGMENT IDS it spans
            — "startSegmentId" and "endSegmentId" — and the exact in/out points are computed
            from the transcript. A clip must begin at the start of a segment, never mid-sentence.

            Aim for {{MinClipSeconds}}-{{MaxClipSeconds}} seconds of segments; 20-45 seconds is
            the sweet spot. Pick moments that stand alone with no setup: a concrete claim, a
            number, a story beat, a contrarian take. If the first segment is throat-clearing
            ("so, yeah, anyway"), start at the next one. Prefer ending on the payoff rather
            than on a trailing "…and so, you know".

            Also score each clip 0-10 on:
              "hook": would this stop someone scrolling in the first two seconds?
              "selfContained": could someone who started here follow it with no prior context?
              "payoff": does it deliver something, or just trail off?
              "emotion": does it land — surprise, conviction, humour, relief?

            For each clip also write the copy its upload form needs:
              "clipTitle": under 100 characters, no clickbait, states the payoff.
              "description": 1-2 sentences.
              "hashtags": 2-5 tags, no leading '#'.
            JSON schema: { "title": string, "clips": [ { "startSegmentId": string, "endSegmentId": string, "hook": string, "clipTitle": string, "description": string, "hashtags": string[], "platformFit": string[], "scores": { "hook": number, "selfContained": number, "payoff": number, "emotion": number } } ], "citations": string[] }
            """,
            ValidateClips,
            ClipBoundaries.Apply));

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
            Every rendered image is centre-cropped afterwards, so compose each prompt for the
            centre of the frame: demand generous clear margins on all four edges and keep every
            key subject inside the central safe area. Prompts must request NO rendered words,
            letters, numbers, captions, headlines, labels, badges or logos. Reserve clean
            negative space for exact text to be composited after generation.
            """,
            (json, t) => ValidateCommon(json, t, requireArray: "images", minItems: 3)));

        specs.Add(new GeneratorSpec(
            "thumbnail-concepts",
            """
            Create 3-5 DISTINCT YouTube thumbnail concepts before any pixels are rendered.
            Each concept must express a different visual angle, not a minor colour variation.
            Ground every concept in approved evidence and the campaign's saved SEO/AEO analysis.

            For each concept return:
              "name": a short working name;
              "angle": the single idea or tension the image communicates;
              "prompt": a production-ready image prompt with composition, subject, lighting,
                        negative space and visual hierarchy, but NO rendered words. The render
                        is centre-cropped, so compose for the centre and demand generous clear
                        margins on every edge — nothing meaningful may sit near an edge;
              "overlayText": 2-5 words, no more than 32 characters, to composite after generation;
              "reason": why this concept supports the primary query and earns a click without clickbait.

            JSON schema: { "title": string, "concepts": [ { "name": string, "angle": string, "prompt": string, "overlayText": string, "reason": string } ], "citations": string[] }
            """,
            (json, t) => ValidateCommon(json, t, requireArray: "concepts", minItems: 3)));

        return specs;
    }

    // ---- Validators ---------------------------------------------------------

    public static ValidationOutcome ValidateCitations(
        JsonElement json, TranscriptContent transcript) =>
        ValidateCitations(json, GenerationEvidenceContext.FromTranscript(transcript));

    public static ValidationOutcome ValidateCitations(
        JsonElement json, GenerationEvidenceContext evidence)
    {
        return evidence.TryNormalizeCitations(json, out _, out var error)
            ? new ValidationOutcome(true, [])
            : new ValidationOutcome(false, [], error);
    }

    /// <summary>
    /// The provenance contract every generator shares. Internal rather than private so the
    /// Tech Edit can hold an unregistered kind to it instead of waving the pass through.
    /// </summary>
    internal static ValidationOutcome ValidateCommon(
        JsonElement json, GenerationEvidenceContext evidence,
        string? requireString = null, string? requireArray = null, int minItems = 0)
    {
        var citations = ValidateCitations(json, evidence);
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

    internal static ValidationOutcome ValidateCommon(
        JsonElement json, TranscriptContent transcript,
        string? requireString = null, string? requireArray = null, int minItems = 0) =>
        ValidateCommon(
            json,
            GenerationEvidenceContext.FromTranscript(transcript),
            requireString,
            requireArray,
            minItems);

    private static ValidationOutcome ValidateEmailSequence(JsonElement json, GenerationEvidenceContext evidence)
    {
        var common = ValidateCommon(json, evidence, requireArray: "emails", minItems: 3);
        if (!common.Passed)
        {
            return common;
        }

        var hasPlaceholder = false;
        foreach (var email in json.GetProperty("emails").EnumerateArray())
        {
            if (!email.TryGetProperty("bodyMarkdown", out var bodyNode)
                || bodyNode.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(bodyNode.GetString()))
            {
                return new ValidationOutcome(false, [], "Every email needs a non-empty bodyMarkdown field.");
            }

            var body = bodyNode.GetString()!;
            if (ContainsYoutubeUrl(body))
            {
                return new ValidationOutcome(false, [],
                    $"Email copy must use {EmailVideoPlaceholder} instead of a YouTube URL.");
            }
            hasPlaceholder |= body.Contains(EmailVideoPlaceholder, StringComparison.Ordinal);
        }

        return hasPlaceholder
            ? common
            : new ValidationOutcome(false, [],
                $"At least one email body must contain the literal {EmailVideoPlaceholder}.");
    }

    private static ValidationOutcome ValidateNewsletter(JsonElement json, GenerationEvidenceContext evidence)
    {
        var common = ValidateCommon(json, evidence, requireString: "bodyMarkdown");
        if (!common.Passed)
        {
            return common;
        }

        var body = json.GetProperty("bodyMarkdown").GetString()!;
        if (ContainsYoutubeUrl(body))
        {
            return new ValidationOutcome(false, [],
                $"Newsletter copy must use {EmailVideoPlaceholder} instead of a YouTube URL.");
        }
        return body.Contains(EmailVideoPlaceholder, StringComparison.Ordinal)
            ? common
            : new ValidationOutcome(false, [],
                $"Newsletter bodyMarkdown must contain the literal {EmailVideoPlaceholder}.");
    }

    private static bool ContainsYoutubeUrl(string text)
    {
        foreach (Match match in HttpUrl().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var host = uri.IdnHost.TrimEnd('.');
            if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase)
                || host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    [GeneratedRegex("""https?://[^\s<>\[\]()"'`]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrl();

    private static ValidationOutcome ValidateSocial(JsonElement json, GenerationEvidenceContext evidence, int cap)
    {
        var common = ValidateCommon(json, evidence, requireString: "text");
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

    private static ValidationOutcome ValidateClips(JsonElement json, GenerationEvidenceContext evidence)
    {
        var common = ValidateCommon(json, evidence, requireArray: "clips", minItems: 1);
        if (!common.Passed)
        {
            return common;
        }
        var maxEnd = evidence.Transcript.Segments.Max(s => s.EndSeconds);
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

    private static ValidationOutcome ValidateSeoBrief(JsonElement json, GenerationEvidenceContext evidence)
    {
        var common = ValidateCommon(json, evidence, requireString: "summary", requireArray: "focusKeywords", minItems: 3);
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

    /// <summary>The complete YouTube package contract. These checks are deterministic so a
    /// polished audit response cannot silently omit the A/B/C experiment or pinned comment.</summary>
    internal static JsonElement NormalizeYoutubeTitleOptions(JsonElement json)
    {
        if (!json.TryGetProperty("titleOptions", out var optionsElement)
            || optionsElement.ValueKind != JsonValueKind.Array
            || optionsElement.GetArrayLength() != 3
            || JsonNode.Parse(json.GetRawText()) is not JsonObject root
            || root["titleOptions"] is not JsonArray options
            || options.Any(option => option is not JsonObject))
        {
            return json;
        }

        var expectedSlots = new[] { "A", "B", "C" };
        var titleOptions = options.Select(option => (JsonObject)option!.DeepClone()).ToList();
        var bySlot = titleOptions
            .Select(option => (Option: option, Slot: CanonicalSlot(NodeString(option["slot"]))))
            .Where(item => item.Slot is not null)
            .ToList();
        if (bySlot.Count == 3 && bySlot.Select(item => item.Slot).Distinct(StringComparer.Ordinal).Count() == 3)
        {
            titleOptions = expectedSlots
                .Select(slot => bySlot.Single(item => item.Slot == slot).Option)
                .ToList();
        }

        var seenAngles = new HashSet<string>(StringComparer.Ordinal);
        var fallbackAngles = new[] { "seo", "curiosity", "problem-solution" };
        for (var index = 0; index < titleOptions.Count; index++)
        {
            var option = titleOptions[index];
            option["slot"] = expectedSlots[index];
            var angle = CanonicalAngle(NodeString(option["angle"]));
            if (angle is null || !seenAngles.Add(angle))
            {
                angle = fallbackAngles.First(candidate => !seenAngles.Contains(candidate));
                seenAngles.Add(angle);
            }
            option["angle"] = angle;
        }

        root["titleOptions"] = new JsonArray(titleOptions.Select(option => (JsonNode)option).ToArray());
        return JsonSerializer.SerializeToElement(root);
    }

    private static string? NodeString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? CanonicalSlot(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "A" => "A",
        "B" => "B",
        "C" => "C",
        _ => null,
    };

    private static string? CanonicalAngle(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant()
            .Replace('_', '-')
            .Replace(' ', '-')
            .Replace('/', '-');
        return normalized switch
        {
            "seo" or "seo-focused" or "seo-optimized" or "seo-optimised"
                or "search-engine-optimization" or "search-engine-optimisation" => "seo",
            "curiosity" or "curiosity-gap" => "curiosity",
            "how-to" or "howto" => "how-to",
            "problem-solution" => "problem-solution",
            "thought-leadership" => "thought-leadership",
            _ => null,
        };
    }

    internal static ValidationOutcome ValidateYoutube(JsonElement json, GenerationEvidenceContext evidence)
    {
        var common = ValidateCommon(json, evidence, requireString: "description");
        if (!common.Passed)
        {
            return common;
        }
        if (!json.TryGetProperty("titleOptions", out var options)
            || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() != 3)
        {
            return new ValidationOutcome(false, [], "Exactly three scored A/B/C titleOptions are required.");
        }

        var expectedSlots = new[] { "A", "B", "C" };
        var allowedAngles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "seo", "curiosity", "how-to", "problem-solution", "thought-leadership" };
        var seenAngles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var option in options.EnumerateArray())
        {
            var slot = option.TryGetProperty("slot", out var slotNode) ? slotNode.GetString() : null;
            var angle = option.TryGetProperty("angle", out var angleNode) ? angleNode.GetString() : null;
            var title = option.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
            if (!string.Equals(slot, expectedSlots[index], StringComparison.OrdinalIgnoreCase)
                || angle is null || !allowedAngles.Contains(angle) || !seenAngles.Add(angle))
            {
                return new ValidationOutcome(false, [],
                    "titleOptions must be A/B/C and use three distinct supported angle-taxonomy values.");
            }
            if (string.IsNullOrWhiteSpace(title) || title.Length > 100
                || !option.TryGetProperty("score", out var scoreNode)
                || !scoreNode.TryGetDouble(out var score) || score is < 0 or > 100)
            {
                return new ValidationOutcome(false, [],
                    $"Title slot {expectedSlots[index]} needs a 1-100 character title and 0-100 score.");
            }
            index++;
        }

        if (!json.TryGetProperty("chapters", out var chapters)
            || chapters.ValueKind != JsonValueKind.Array || chapters.GetArrayLength() < 3)
        {
            return new ValidationOutcome(false, [], "At least three keyworded YouTube chapters are required.");
        }
        var first = chapters[0];
        if (!first.TryGetProperty("startSeconds", out var start) || start.GetDouble() != 0)
        {
            return new ValidationOutcome(false, [], "YouTube chapters must start at 0:00.");
        }
        if (!json.TryGetProperty("suggestedPinnedComment", out var commentNode)
            || commentNode.ValueKind != JsonValueKind.String
            || commentNode.GetString() is not { Length: > 10 } comment
            || !comment.TrimEnd().EndsWith('?'))
        {
            return new ValidationOutcome(false, [], "The suggested pinned comment must be substantive and end with a question.");
        }

        var warnings = new List<string>();
        var description = json.GetProperty("description").GetString()!;
        var opening = description.Length <= 125 ? description : description[..125];
        if (opening.Contains('#', StringComparison.Ordinal))
        {
            warnings.Add("A hashtag appears in the first 125 characters; hoist it to the final line.");
        }
        if (description.Length > 0 && description[..Math.Min(125, description.Length)].Trim().Length < 45)
        {
            warnings.Add("The first 125 characters may be too thin to work as a search hook.");
        }
        return new ValidationOutcome(true, warnings);
    }

    internal static ValidationOutcome ValidateYoutube(JsonElement json, TranscriptContent transcript) =>
        ValidateYoutube(json, GenerationEvidenceContext.FromTranscript(transcript));

    /// <summary>Blog validator (B5.2): word band + citations.</summary>
    public static ValidationOutcome ValidateBlog(JsonElement json, GenerationEvidenceContext evidence)
    {
        var common = ValidateCommon(json, evidence, requireString: "markdown");
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

    public static ValidationOutcome ValidateBlog(JsonElement json, TranscriptContent transcript) =>
        ValidateBlog(json, GenerationEvidenceContext.FromTranscript(transcript));
}
