using System.Text.Json;
using Castmill.UI.Editor;

namespace Castmill.UI.Tests;

public sealed class BlogPublishingMetadataTests
{
    [Fact]
    public void Metadata_is_saved_inside_the_blog_without_losing_markdown_or_citations()
    {
        const string original = """
            {"content":{"markdown":"# Article","metaDescription":"Generated description","citations":["s01"]},"publishingMetadata":{"futureField":{"mode":"strict"}},"validation":{"passed":true}}
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
        Assert.Contains("\"futureField\":{\"mode\":\"strict\"}", saved, StringComparison.Ordinal);
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

    [Fact]
    public void Canonical_url_is_built_from_a_safe_site_url_and_normalized_slug()
    {
        Assert.Equal(
            "https://example.com/blog/launch-day-details",
            BlogMetadataBuilder.BuildCanonicalUrl(
                "https://example.com/blog", "Launch Day / Details", "Ignored title"));
        Assert.Null(BlogMetadataBuilder.BuildCanonicalUrl(
            "javascript:alert(1)", "launch", "Ignored title"));
    }

    [Fact]
    public void Outputs_encode_html_and_emit_article_video_and_visible_faq_schema()
    {
        const string videoUrl = "https://www.youtube.com/watch?v=abc123";
        const string markdown = """
            # Launch <guide>

            Watch the walkthrough at https://www.youtube.com/watch?v=abc123.

            ## Frequently Asked Questions

            ### Does this preserve unknown metadata?

            Yes, existing fields remain on the owning blog.
            """;
        var output = BlogMetadataBuilder.Build(new BlogMetadataDocument(
            "Launch <guide>",
            markdown,
            DateTimeOffset.Parse("2026-08-19T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new BlogPublishingMetadata
            {
                Description = "Ship safely & deliberately.",
                SiteUrl = "https://example.com/blog",
                Slug = "launch-guide",
                SiteName = "Example & Co",
                OrganizationName = "Example Org",
                OrganizationLogoUrl = "https://example.com/logo.png",
                Author = "A. Writer",
                VideoUrl = videoUrl,
            }));

        Assert.Contains("<title>Launch &lt;guide&gt;</title>", output.HtmlHead, StringComparison.Ordinal);
        Assert.Contains("Example &amp; Co", output.HtmlHead, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>Launch <guide>", output.HtmlHead, StringComparison.Ordinal);
        Assert.Contains("<script type=\"application/ld+json\">", output.Combined, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(output.JsonLdOnly);
        var graph = json.RootElement.GetProperty("@graph").EnumerateArray().ToList();
        Assert.Contains(graph, node => node.GetProperty("@type").GetString() == "Article");
        Assert.Contains(graph, node => node.GetProperty("@type").GetString() == "VideoObject");
        var faq = Assert.Single(graph, node => node.GetProperty("@type").GetString() == "FAQPage");
        Assert.Equal("Does this preserve unknown metadata?",
            faq.GetProperty("mainEntity")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Schema_omits_faq_and_video_that_are_not_visible_in_the_blog()
    {
        const string hiddenVideo = "https://www.youtube.com/watch?v=not-visible";
        var output = BlogMetadataBuilder.Build(new BlogMetadataDocument(
            "Article",
            $$"""
            ## Details

            ### Is this a question?

            An answer outside a visible FAQ section.

            <!-- {{hiddenVideo}} -->

            ```text
            {{hiddenVideo}}
            ```

            [hidden-video]: {{hiddenVideo}}
            """,
            DateTimeOffset.UtcNow,
            new BlogPublishingMetadata
            {
                VideoUrl = hiddenVideo,
            }));

        using var json = JsonDocument.Parse(output.JsonLdOnly);
        var types = json.RootElement.GetProperty("@graph").EnumerateArray()
            .Select(node => node.GetProperty("@type").GetString()).ToList();
        Assert.DoesNotContain("VideoObject", types);
        Assert.DoesNotContain("FAQPage", types);
    }

    [Fact]
    public void Faq_questions_are_relative_to_the_visible_faq_heading_level()
    {
        var faq = BlogMetadataBuilder.VisibleFaq(
            "# FAQ\n\n## Can questions use level two?\n\nYes, beneath a level-one FAQ heading.");

        var entry = Assert.Single(faq);
        Assert.Equal("Can questions use level two?", entry.Question);
    }
}
