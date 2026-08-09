using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.State;

namespace Castmill.UI.Tests;

/// <summary>
/// The cluster projection turns "a pile of artifacts" into "a pillar and the content that
/// reinforces it" — the model the cluster map draws. If this is wrong, the map lies about how
/// the campaign hangs together.
/// </summary>
public sealed class ContentClusterTests
{
    private static ArtifactPreviewResponse Artifact(string kind, string title = "t", string status = "Draft") =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, title, status, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ContentCluster Build(
        IReadOnlyList<ArtifactPreviewResponse> artifacts, SeoTargetsResponse? targets = null) =>
        ContentCluster.Build(artifacts, targets, ArtifactDisplay.KindLabel);

    [Fact]
    public void The_blog_is_the_pillar_and_everything_else_supports_it()
    {
        var cluster = Build(
            [Artifact("blog", "Enterprise data grids"), Artifact("social-x"), Artifact("newsletter")],
            new SeoTargetsResponse("react data grid", [], []));

        Assert.NotNull(cluster.Pillar);
        Assert.True(cluster.Pillar!.IsPillar);
        Assert.Equal("Enterprise data grids", cluster.Pillar.Title);
        Assert.Equal("react data grid", cluster.PrimaryKeyword);

        // The two non-blog pieces are supporting, and the pillar is NOT among them.
        Assert.Equal(2, cluster.Supporting.Count);
        Assert.DoesNotContain(cluster.Supporting, n => n.ArtifactId == cluster.Pillar.ArtifactId);
    }

    [Fact]
    public void Non_board_kinds_are_not_nodes()
    {
        // image-prompts and transcript are machinery, not content — they must not clutter the
        // map, exactly as they are absent from the board.
        var cluster = Build([Artifact("blog"), Artifact("image-prompts"), Artifact("transcript")]);

        Assert.NotNull(cluster.Pillar);
        Assert.Empty(cluster.Supporting);
    }

    [Fact]
    public void Missing_channels_surface_as_gaps_present_ones_do_not()
    {
        var cluster = Build([Artifact("blog"), Artifact("youtube"), Artifact("social-linkedin")]);

        var gapKinds = cluster.Gaps.Select(g => g.Kind).ToList();
        // youtube and linkedin exist, so they are not gaps…
        Assert.DoesNotContain("youtube", gapKinds);
        Assert.DoesNotContain("social-linkedin", gapKinds);
        // …while a channel the campaign lacks is offered as one.
        Assert.Contains("newsletter", gapKinds);
    }

    [Fact]
    public void With_no_blog_but_other_content_there_is_no_pillar_yet()
    {
        var cluster = Build([Artifact("social-x"), Artifact("newsletter")]);

        Assert.Null(cluster.Pillar);
        Assert.Equal(2, cluster.Supporting.Count);
        Assert.False(cluster.IsEmpty); // still worth drawing — a cluster forming without a centre
    }

    [Fact]
    public void An_empty_campaign_produces_an_empty_cluster()
    {
        var cluster = Build([]);
        Assert.True(cluster.IsEmpty);
    }

    [Fact]
    public void Questions_ride_along_from_the_saved_targets()
    {
        var cluster = Build(
            [Artifact("blog")],
            new SeoTargetsResponse("k", [], [new SeoQuestion("What is a data grid?", "paa")]));

        Assert.Single(cluster.Questions);
        Assert.Equal("What is a data grid?", cluster.Questions[0].Question);
    }
}
