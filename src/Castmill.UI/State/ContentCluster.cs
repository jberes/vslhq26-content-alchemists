using Castmill.Core.Resources;

namespace Castmill.UI.State;

/// <summary>
/// The campaign as a topic cluster: one PILLAR (the primary blog) as the hierarchy root, every
/// other artifact as supporting content, and the SEO targets and questions laid over it so gaps
/// are visible. This is the model behind the cluster-map visualisation — it turns "a pile of
/// artifacts" into "a pillar and the content that reinforces it", which is what makes a
/// campaign an SEO campaign rather than a heap of one-offs.
///
/// Pure projection over data the client already holds (artifacts + saved targets): no server
/// round-trip, with stable IDs so ApexTree preserves a coherent hierarchy across renders.
/// </summary>
public sealed record ClusterNode(
    Guid ArtifactId,
    string Kind,
    string KindLabel,
    string Title,
    string Status,
    bool IsPillar);

public sealed record ClusterGap(string Kind, string KindLabel);

public sealed record ContentCluster(
    string? PrimaryKeyword,
    ClusterNode? Pillar,
    IReadOnlyList<ClusterNode> Supporting,
    IReadOnlyList<ClusterGap> Gaps,
    IReadOnlyList<SeoQuestion> Questions)
{
    public bool IsEmpty => Pillar is null && Supporting.Count == 0;

    /// <summary>
    /// The kinds a healthy cluster around a pillar wants: the reach channels and the
    /// answer-surfaces. Not every campaign needs all of them, but a MISSING one is worth
    /// showing as an "add this" affordance rather than leaving invisible.
    /// </summary>
    private static readonly (string Kind, string Label)[] DesiredSupport =
    [
        ("youtube", "YouTube package"),
        ("social-linkedin", "LinkedIn post"),
        ("social-x", "X post"),
        ("newsletter", "Newsletter"),
        ("email-sequence", "Email sequence"),
        ("landing-page", "Landing page"),
    ];

    /// <summary>
    /// Builds the cluster. The pillar is the primary blog; if the campaign has blogs but no
    /// targets yet, the newest blog stands in so the map is still useful before research.
    /// </summary>
    public static ContentCluster Build(
        IReadOnlyList<ArtifactPreviewResponse> artifacts,
        SeoTargetsResponse? targets,
        Func<string, string> kindLabel)
    {
        var onBoard = artifacts
            .Where(a => ArtifactDisplay.OnBoard(a.Kind))
            .OrderBy(a => a.CreatedAt)
            .ToList();

        var blog = onBoard.FirstOrDefault(a => a.Kind == "blog");

        ClusterNode? pillar = blog is null
            ? null
            : new ClusterNode(blog.Id, blog.Kind, kindLabel(blog.Kind), blog.Title, blog.Status, IsPillar: true);

        var supporting = onBoard
            .Where(a => pillar is null || a.Id != pillar.ArtifactId)
            .Select(a => new ClusterNode(a.Id, a.Kind, kindLabel(a.Kind), a.Title, a.Status, IsPillar: false))
            .ToList();

        var present = onBoard.Select(a => a.Kind).ToHashSet(StringComparer.Ordinal);
        var gaps = DesiredSupport
            .Where(d => !present.Contains(d.Kind))
            .Select(d => new ClusterGap(d.Kind, d.Label))
            .ToList();

        return new ContentCluster(
            targets?.PrimaryKeyword,
            pillar,
            supporting,
            gaps,
            targets?.Questions ?? []);
    }
}
