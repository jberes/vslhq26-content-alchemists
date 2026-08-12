namespace Castmill.Core;

/// <summary>
/// Product-level content inventory shared by API and clients. These are pieces a person can
/// intentionally generate and govern with a Brand Template. System artifacts (campaign
/// summary, transcript, SEO report/plan/research pass) and operational image-planning artifacts
/// are not content choices even though some are persisted or model-generated internally.
/// </summary>
public static class ArtifactKinds
{
    public static readonly string[] UserContent =
    [
        "blog", "youtube", "show-notes",
        "social-x", "social-linkedin", "social-facebook", "social-instagram",
        "social-threads", "social-bluesky",
        "email-sequence", "newsletter", "clip-suggestions", "landing-page",
    ];

    public static bool IsUserContent(string kind) =>
        UserContent.Contains(kind, StringComparer.Ordinal);

    /// <summary>
    /// Finished pieces that can be distributed to an audience. Clip suggestions are generated
    /// working instructions, not a publishable campaign deliverable; exported clips become
    /// distributable media through the clip pipeline.
    /// </summary>
    public static readonly string[] DistributionContent =
        UserContent.Where(kind => kind != "clip-suggestions").ToArray();

    public static bool IsDistributionContent(string kind) =>
        DistributionContent.Contains(kind, StringComparer.Ordinal);

    /// <summary>
    /// The channel family a kind belongs to, in the order a cluster reads: the long-form
    /// surface that carries the search intent, then video, then the social distribution, then
    /// owned audience. Lives in Core rather than a view because the same grouping labels the
    /// content hierarchy, and anything that lists kinds by family should agree with it.
    /// </summary>
    public static readonly (string Key, string Label, string[] Kinds)[] Categories =
    [
        ("long-form", "Long-form", ["blog", "landing-page", "show-notes"]),
        ("video", "Video", ["youtube", "clip-suggestions"]),
        ("social", "Social", [
            "social-x", "social-linkedin", "social-facebook", "social-instagram",
            "social-threads", "social-bluesky"]),
        ("owned", "Owned audience", ["newsletter", "email-sequence"]),
    ];

    /// <summary>Category key for a kind; "other" for anything not yet filed.</summary>
    public static string CategoryOf(string kind) =>
        Array.Find(Categories, c => c.Kinds.Contains(kind, StringComparer.Ordinal)).Key ?? "other";

    public static string CategoryLabel(string categoryKey) =>
        Array.Find(Categories, c => c.Key == categoryKey).Label ?? "Other";

    /// <summary>Display order of a category — unfiled kinds sort last.</summary>
    public static int CategoryOrder(string categoryKey)
    {
        var index = Array.FindIndex(Categories, c => c.Key == categoryKey);
        return index < 0 ? Categories.Length : index;
    }
}
