using Castmill.Core;
using Castmill.UI.State;

namespace Castmill.UI.Tests;

/// <summary>
/// The canonical content inventory drives generation and Brand Templates. This catches the
/// two costly forms of drift: a real generator disappearing from a surface, and a system
/// artifact being offered as if it were a generator.
/// </summary>
public sealed class ContentTypeSurfaceTests
{
    [Fact]
    public void Every_user_content_generator_is_available_to_generation_and_brand_templates()
    {
        Assert.Equal(ArtifactKinds.UserContent.Order(),
            ArtifactDisplay.UserGeneratableKinds.Select(kind => kind.Kind).Order());
        Assert.Equal(ArtifactKinds.UserContent.Order(),
            ArtifactDisplay.BrandTemplateKinds.Select(kind => kind.Kind).Order());
    }

    [Fact]
    public void System_and_operational_artifacts_are_visible_in_their_workspaces_but_not_creation_menus()
    {
        var nonGeneratable = new[]
        {
            "campaign-summary", "seo-brief", "seo-keyword-plan", "seo-report",
            "image-prompts", "thumbnail-concepts", "transcript",
        };

        Assert.All(nonGeneratable, kind =>
        {
            var info = ArtifactDisplay.Resolve(kind);
            Assert.False(info.UserGeneratable);
            Assert.False(info.BrandTemplatable);
        });
        Assert.True(ArtifactDisplay.OnBoard("campaign-summary"));
        Assert.False(ArtifactDisplay.OnBoard("seo-brief"));
        Assert.False(ArtifactDisplay.Editable("seo-brief"));
        Assert.False(ArtifactDisplay.OnBoard("seo-keyword-plan"));
        Assert.False(ArtifactDisplay.Editable("seo-keyword-plan"));
        Assert.False(ArtifactDisplay.OnBoard("seo-report"));
    }

    [Fact]
    public void Every_user_content_kind_has_an_explicit_distribution_role()
    {
        Assert.Equal(
            ArtifactKinds.UserContent.Except(["clip-suggestions"]),
            ArtifactKinds.DistributionContent);
        Assert.True(ArtifactKinds.IsDistributionContent("youtube"));
        Assert.False(ArtifactKinds.IsDistributionContent("clip-suggestions"));
        Assert.False(ArtifactKinds.IsUserContent("seo-brief"));
        Assert.False(ArtifactKinds.IsDistributionContent("campaign-summary"));
    }

    [Fact]
    public void Every_campaign_format_has_a_human_label()
    {
        Assert.Equal(4, CampaignContentType.All.Length);
        Assert.All(CampaignContentType.All, type =>
            Assert.NotEqual("Campaign", CampaignContentType.DisplayLabel(type)));
        Assert.Equal("Product demo", CampaignContentType.DisplayLabel(CampaignContentType.ProductDemo));
        Assert.Equal("Thought leadership", CampaignContentType.DisplayLabel(CampaignContentType.ThoughtLeadership));
    }
}
