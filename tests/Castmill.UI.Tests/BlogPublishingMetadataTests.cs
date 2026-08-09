using Castmill.UI.Editor;

namespace Castmill.UI.Tests;

public sealed class BlogPublishingMetadataTests
{
    [Fact]
    public void Metadata_is_saved_inside_the_blog_without_losing_markdown_or_citations()
    {
        const string original = """
            {"content":{"markdown":"# Article","metaDescription":"Generated description","citations":["s01"]},"validation":{"passed":true}}
            """;
        var metadata = new BlogPublishingMetadata
        {
            CanonicalUrl = "https://example.com/article",
            SiteName = "Example",
            Author = "A. Writer",
            Description = "Approved description",
            Keywords = "react data grid, virtualization",
        };

        var saved = ArtifactContent.WithPublishingMetadata(original, metadata);
        var roundTrip = ArtifactContent.PublishingMetadata(saved);

        Assert.Equal("# Article", ArtifactContent.ToMarkdown(saved));
        Assert.Contains("\"citations\":[\"s01\"]", saved, StringComparison.Ordinal);
        Assert.Equal(metadata.CanonicalUrl, roundTrip.CanonicalUrl);
        Assert.Equal(metadata.Description, roundTrip.Description);
        Assert.Equal(metadata.Keywords, roundTrip.Keywords);
    }

    [Fact]
    public void Generated_meta_description_is_the_blog_default_until_the_user_changes_it()
    {
        var metadata = ArtifactContent.PublishingMetadata(
            """{"content":{"markdown":"body","metaDescription":"Source default"}}""");

        Assert.Equal("Source default", metadata.Description);
    }
}
