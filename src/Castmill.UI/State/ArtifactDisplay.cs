using Castmill.Core;

namespace Castmill.UI.State;

/// <summary>
/// One place that turns artifact data into display strings. Kinds and statuses appear on the
/// front page, the canvas, Focus Mode and the Wire; if each surface formatted them itself
/// they would drift, which is the same argument as the status tokens in ADR-F12.
/// </summary>
public static class ArtifactDisplay
{
    /// <summary>Status label as shown to a person.</summary>
    public static string StatusLabel(string status) => status switch
    {
        ArtifactStatus.InReview => "In review",
        ArtifactStatus.Queued => "Queued",
        ArtifactStatus.Published => "Published",
        _ => "Draft",
    };

    /// <summary>
    /// CSS modifier for the status encoding. Pairs with <c>.cm-status</c>, which renders the
    /// 3 px bar as well as the colour so state never depends on hue alone (ADR-F12).
    /// </summary>
    public static string StatusModifier(string status) => status switch
    {
        ArtifactStatus.InReview => "cm-status cm-status--review",
        ArtifactStatus.Queued => "cm-status cm-status--queued",
        ArtifactStatus.Published => "cm-status cm-status--published",
        _ => "cm-status",
    };

    /// <summary>
    /// Everything the client knows about one artifact kind. This registry is the single
    /// source of truth — the lane map, the labels, Focus's editable list and the board
    /// filter all read it, because three hand-maintained lists drifted (show-notes fell
    /// through a lane switch's default arm and rendered inside the Social lane).
    /// </summary>
    public sealed record KindInfo(string Kind, string Label, string Lane, bool Editable, bool OnBoard);

    /// <summary>Board lanes in display order. "Other" catches kinds this build doesn't know.</summary>
    // YouTube on top: the package this app exists to produce reads first on the board.
    public static readonly string[] LaneOrder =
        ["YouTube", "Blog", "Social", "Email", "Clips", "Page/SEO", "Other"];

    /// <summary>
    /// The registry, in display order (in-lane sub-groups follow this order). Images are
    /// deliberately absent from the board: image work lives in the Image Studio and inside
    /// the content itself, so <c>image-prompts</c> and <c>transcript</c> carry
    /// <c>OnBoard: false</c>.
    /// </summary>
    public static readonly KindInfo[] Known =
    [
        new("blog", "Blog post", "Blog", Editable: true, OnBoard: true),
        // Directly after blog so derived choice lists (fan-out, print-more chips) offer it
        // second; its lane position comes from LaneOrder, not from this row's position.
        new("youtube", "YouTube package", "YouTube", Editable: true, OnBoard: true),
        new("show-notes", "Show notes", "Blog", Editable: true, OnBoard: true),
        new("social-x", "X post", "Social", Editable: true, OnBoard: true),
        new("social-linkedin", "LinkedIn post", "Social", Editable: true, OnBoard: true),
        new("social-facebook", "Facebook post", "Social", Editable: true, OnBoard: true),
        new("social-instagram", "Instagram post", "Social", Editable: true, OnBoard: true),
        new("social-threads", "Threads post", "Social", Editable: true, OnBoard: true),
        new("social-bluesky", "Bluesky post", "Social", Editable: true, OnBoard: true),
        new("email-sequence", "Email sequence", "Email", Editable: true, OnBoard: true),
        new("newsletter", "Newsletter", "Email", Editable: true, OnBoard: true),
        new("clip-suggestions", "Clip suggestions", "Clips", Editable: true, OnBoard: true),
        new("landing-page", "Landing page", "Page/SEO", Editable: true, OnBoard: true),
        new("seo-brief", "SEO brief", "Page/SEO", Editable: true, OnBoard: true),
        new("seo-keyword-plan", "Keyword plan", "Page/SEO", Editable: true, OnBoard: true),
        new("image-prompts", "Image prompts", "Images", Editable: false, OnBoard: false),
        new("thumbnail-concepts", "Thumbnail concepts", "Images", Editable: false, OnBoard: false),
        new("transcript", "Transcript", "Source", Editable: false, OnBoard: false),
    ];

    /// <summary>
    /// Resolves a kind to its registry entry, tolerating the legacy spellings the database
    /// may still hold. Unknown kinds resolve to an editable "Other"-lane entry rather than
    /// disappearing — new server kinds must show up somewhere before the registry learns them.
    /// </summary>
    public static KindInfo Resolve(string kind)
    {
        var canonical = kind switch
        {
            "clips" => "clip-suggestions",
            "keyword-plan" => "seo-keyword-plan",
            _ => kind,
        };

        return Known.FirstOrDefault(k => k.Kind == canonical)
            ?? new KindInfo(kind, Prettify(kind), "Other", Editable: true, OnBoard: true);
    }

    /// <summary>The six per-platform social generator kinds — "social" alone matches nothing.</summary>
    public static readonly string[] SocialKinds =
        [.. Known.Where(k => k.Lane == "Social").Select(k => k.Kind)];

    /// <summary>Human label for an artifact kind, keyed to the generator names the API uses.</summary>
    public static string KindLabel(string kind) => Resolve(kind).Label;

    /// <summary>Which swimlane a kind belongs to on the Mill Floor (F5) and the front page.</summary>
    public static string Lane(string kind) => Resolve(kind).Lane;

    /// <summary>Whether Focus mode can open this kind for editing.</summary>
    public static bool Editable(string kind) => Resolve(kind).Editable;

    /// <summary>Whether the Mill Floor board shows this kind as a card.</summary>
    public static bool OnBoard(string kind) => Resolve(kind).OnBoard;

    /// <summary>Registry position, so in-lane sub-groups render in a stable, intended order.</summary>
    public static int KindOrder(string kind)
    {
        var resolved = Resolve(kind);
        var index = Array.FindIndex(Known, k => k.Kind == resolved.Kind);
        return index >= 0 ? index : Known.Length;
    }

    private static string Prettify(string kind) =>
        kind.Replace('-', ' ') is { Length: > 0 } pretty
            ? char.ToUpperInvariant(pretty[0]) + pretty[1..]
            : kind;

    /// <summary>"3 days ago" — deliberately coarse; exact timestamps live in tooltips.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;

        return span switch
        {
            _ when span < TimeSpan.FromMinutes(1) => "just now",
            _ when span < TimeSpan.FromHours(1) => $"{(int)span.TotalMinutes} min ago",
            _ when span < TimeSpan.FromDays(1) => $"{(int)span.TotalHours} h ago",
            _ when span < TimeSpan.FromDays(2) => "yesterday",
            _ when span < TimeSpan.FromDays(30) => $"{(int)span.TotalDays} days ago",
            _ => when.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
        };
    }

    /// <summary>Slot label for the image plan, e.g. "YouTube thumbnail".</summary>
    public static string SlotLabel(string kind) => kind switch
    {
        "youtube-thumbnail" => "YouTube thumbnail",
        "blog-hero" => "Blog header",
        "social-card" => "Social card",
        _ when kind.StartsWith("inline", StringComparison.Ordinal) =>
            "Inline " + kind.Replace("inline-", string.Empty, StringComparison.Ordinal),
        _ => KindLabel(kind),
    };

    /// <summary>
    /// Resolves cited segment ids to the ids the transcript ACTUALLY uses, dropping any that
    /// match nothing.
    ///
    /// Load-bearing, not tidiness. Segment ids are lower-case and zero-padded ("s02") when a
    /// transcript comes from transcription, but a model routinely cites them as "S02" — and
    /// ValidateCitations compares case-INsensitively, so that passes validation and is stored
    /// verbatim. The provenance overlay then looks the row up with [data-seg="S02"], and CSS
    /// attribute selectors ARE case-sensitive, so every thread found nothing and the connector
    /// lines silently stopped drawing.
    /// </summary>
    public static IReadOnlyList<string> ResolveCitations(
        IReadOnlyList<string>? cited, IReadOnlyList<Castmill.Core.Ai.TranscriptSegment>? segments)
    {
        if (cited is null || cited.Count == 0 || segments is null || segments.Count == 0)
        {
            return cited ?? [];
        }

        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            canonical[segment.Id] = segment.Id;
        }

        // A thread to a segment that does not exist would be a line pointing at nothing.
        return [.. cited
            .Select(c => canonical.TryGetValue(c, out var real) ? real : null)
            .Where(id => id is not null)
            .Select(id => id!)];
    }
}
