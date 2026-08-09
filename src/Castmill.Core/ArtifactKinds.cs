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
}
