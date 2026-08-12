using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Canvas;
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
    public void Operational_and_strategy_kinds_are_not_nodes()
    {
        // Machinery and internal strategy documents are not outward-facing content and must
        // not clutter the hierarchy even when they are editable elsewhere.
        var cluster = Build([
            Artifact("blog"), Artifact("image-prompts"), Artifact("transcript"),
            Artifact("campaign-summary"), Artifact("seo-keyword-plan"), Artifact("seo-brief")
        ]);

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

    /// <summary>
    /// Pillar → channel family → pieces. The pillar used to own every supporting artifact
    /// directly, which drew a dozen cards as one undifferentiated vertical run: legible per
    /// card, shapeless as a map, and no answer to "which channels does this pillar reach".
    /// </summary>
    [Fact]
    public void ApexTree_groups_supporting_content_by_channel_family_under_the_pillar()
    {
        var blog = Artifact("blog", "Enterprise data grids", "Approved");
        var linkedIn = Artifact("social-linkedin", "The practical rollout", "Draft");
        var x = Artifact("social-x", "Launch thread", "Draft");
        var newsletter = Artifact("newsletter", "This month in grids", "Approved");
        var cluster = Build(
            [blog, linkedIn, x, newsletter],
            new SeoTargetsResponse("react data grid", [], []));

        var tree = ClusterMap.BuildTree(cluster);

        Assert.Equal(blog.Id.ToString("D"), tree.Id);
        Assert.Equal("Pillar blog", tree.Title);
        Assert.Equal("open", tree.Action);
        Assert.Equal("success", tree.Tone);

        // A family node is a label, not a destination: clicking it must do nothing.
        var social = Assert.Single(tree.Children, child => child.Id == "category-social");
        Assert.Equal("Social", social.Name);
        Assert.Equal("2 pieces", social.Title);
        Assert.Equal("none", social.Action);
        Assert.Equal("In progress", social.Badge); // both still drafts
        Assert.Equal(2, social.Children.Count);

        var piece = Assert.Single(social.Children, child => child.Id == linkedIn.Id.ToString("D"));
        Assert.Equal("open", piece.Action);
        Assert.Equal(linkedIn.Id.ToString("D"), piece.Value);

        var owned = Assert.Single(tree.Children, child => child.Id == "category-owned");
        Assert.Equal("1 piece", owned.Title);
        Assert.Equal("Ready", owned.Badge); // the only piece is approved
        Assert.Equal("success", owned.Tone);

        // Long-form leads, then video, then social, then owned audience — reading order.
        Assert.Equal(
            ["category-social", "category-owned", "category-gaps"],
            tree.Children.Select(child => child.Id).ToArray());

        // Gaps collect under one node, so an open channel can never hide between real pieces.
        var gaps = Assert.Single(tree.Children, child => child.Id == "category-gaps");
        Assert.Equal("gap", gaps.Tone);
        var gap = Assert.Single(gaps.Children, child => child.Id == "gap-youtube");
        Assert.Equal("draft", gap.Action);
        Assert.Equal("youtube", gap.Value);
        Assert.Equal("gap", gap.Tone);
    }

    /// <summary>An empty family is omitted rather than drawn as a zero — a card that says
    /// "0 pieces" is noise the map has to be scanned past.</summary>
    [Fact]
    public void ApexTree_omits_channel_families_with_nothing_in_them()
    {
        var blog = Artifact("blog", "Enterprise data grids", "Approved");
        var tree = ClusterMap.BuildTree(Build([blog, Artifact("social-x", "Launch thread")]));

        Assert.Contains(tree.Children, child => child.Id == "category-social");
        Assert.DoesNotContain(tree.Children, child => child.Id == "category-video");
        Assert.DoesNotContain(tree.Children, child => child.Id == "category-owned");
    }

    [Fact]
    public void ApexTree_uses_a_non_actionable_campaign_root_until_a_pillar_exists()
    {
        var social = Artifact("social-x", "Launch thread");
        var tree = ClusterMap.BuildTree(Build([social]));

        Assert.Equal("campaign-root", tree.Id);
        Assert.Equal("none", tree.Action);
        var family = Assert.Single(tree.Children, child => child.Id == "category-social");
        Assert.Contains(family.Children, child => child.Id == social.Id.ToString("D"));
    }
}
