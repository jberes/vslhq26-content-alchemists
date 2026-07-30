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

    /// <summary>Human label for an artifact kind, keyed to the generator names the API uses.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "blog" => "Blog post",
        "landing-page" => "Landing page",
        "email-sequence" => "Email sequence",
        "newsletter" => "Newsletter",
        "show-notes" => "Show notes",
        "clips" => "Clip suggestions",
        "seo-brief" => "SEO brief",
        "keyword-plan" => "Keyword plan",
        "transcript" => "Transcript",
        "image-prompts" => "Image prompts",
        _ => kind.Replace('-', ' ') is { Length: > 0 } pretty
            ? char.ToUpperInvariant(pretty[0]) + pretty[1..]
            : kind,
    };

    /// <summary>Which swimlane a kind belongs to on the Mill Floor (F5) and the front page.</summary>
    public static string Lane(string kind) => kind switch
    {
        "blog" or "landing-page" => "Blog",
        "newsletter" or "email-sequence" => "Email",
        "clips" => "Clips",
        "seo-brief" or "keyword-plan" => "Page/SEO",
        "image-prompts" => "Images",
        "transcript" => "Source",
        _ => "Social",
    };

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
}
