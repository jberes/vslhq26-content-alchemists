using Castmill.Api.Services.Publish;

namespace Castmill.Api.Tests;

/// <summary>
/// Where a published file goes and what its front matter says (ADR-021). These are the parts
/// that silently break somebody's SITE rather than one request: a wrong date format or an
/// unexpected front-matter key fails the build, and confusing "where the bytes went" with
/// "what the markdown says" is the classic broken-image bug.
/// </summary>
public sealed class GitRepoLayoutTests
{
    private static readonly DateTimeOffset Date =
        new(2026, 8, 7, 9, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Jekyll_requires_the_date_prefix_in_the_file_name()
    {
        // Not cosmetic: Jekyll parses the date AND the slug out of the file name, so a file
        // without the prefix is simply not a post.
        var layout = GitRepoLayout.ForPreset("jekyll");

        Assert.Equal("_posts/2026-08-07-how-we-ship.md", layout.ContentFilePath("how-we-ship", Date));
    }

    [Fact]
    public void Each_preset_writes_where_its_generator_actually_looks()
    {
        Assert.Equal("content/posts/how-we-ship.md",
            GitRepoLayout.ForPreset("hugo").ContentFilePath("how-we-ship", Date));
        Assert.Equal("src/content/blog/how-we-ship.md",
            GitRepoLayout.ForPreset("astro").ContentFilePath("how-we-ship", Date));
        Assert.Equal("content/blog/how-we-ship.mdx",
            GitRepoLayout.ForPreset("nextjs").ContentFilePath("how-we-ship", Date));
    }

    /// <summary>
    /// Where bytes are WRITTEN is almost never what the markdown SAYS — Hugo strips
    /// <c>static/</c> and Next serves <c>public/</c> from the root. Conflating them is the
    /// single most common broken-image bug in this kind of integration.
    /// </summary>
    [Fact]
    public void The_image_write_path_and_the_reference_are_allowed_to_differ()
    {
        var hugo = GitRepoLayout.ForPreset("hugo");
        Assert.Equal("static/img/how-we-ship", hugo.ImageDirectory("how-we-ship"));
        Assert.Equal("/img/how-we-ship/hero.webp", hugo.ImageReference("how-we-ship", "hero.webp"));

        var next = GitRepoLayout.ForPreset("nextjs");
        Assert.Equal("public/images/how-we-ship", next.ImageDirectory("how-we-ship"));
        Assert.Equal("/images/how-we-ship/hero.webp", next.ImageReference("how-we-ship", "hero.webp"));
    }

    /// <summary>
    /// Astro validates front matter against a strict schema, so an unexpected key fails the
    /// whole site build. A field mapped to null must be omitted entirely, not emitted empty.
    /// </summary>
    [Fact]
    public void A_field_mapped_to_null_is_omitted_entirely()
    {
        var front = GitRepoLayout.ForPreset("astro").FrontMatter(
            "How we ship", "A description", Date, "how-we-ship",
            draft: false, heroImage: "./how-we-ship/hero.webp", tags: []);

        Assert.Contains("pubDate: \"2026-08-07\"", front, StringComparison.Ordinal);
        Assert.Contains("heroImage:", front, StringComparison.Ordinal);
        Assert.DoesNotContain("slug:", front, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_preset_dates_the_way_its_generator_parses()
    {
        string Date_(string preset) => GitRepoLayout.ForPreset(preset)
            .FrontMatter("t", null, Date, "s", draft: false, heroImage: null, tags: []);

        Assert.Contains("2026-08-07 09:00:00 -04:00", Date_("jekyll"), StringComparison.Ordinal);
        Assert.Contains("2026-08-07T09:00:00-04:00", Date_("hugo"), StringComparison.Ordinal);
        Assert.Contains("pubDate: \"2026-08-07\"", Date_("astro"), StringComparison.Ordinal);
    }

    /// <summary>Some templates gate on published rather than draft — the same idea inverted.</summary>
    [Fact]
    public void Published_false_semantics_invert_the_draft_flag()
    {
        var draft = GitRepoLayout.ForPreset("nextjs").FrontMatter(
            "t", null, Date, "s", draft: true, heroImage: null, tags: []);
        var live = GitRepoLayout.ForPreset("nextjs").FrontMatter(
            "t", null, Date, "s", draft: false, heroImage: null, tags: []);

        Assert.Contains("published: false", draft, StringComparison.Ordinal);
        Assert.Contains("published: true", live, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model-written title routinely contains a colon or a quote. Unescaped, that is a YAML
    /// parse error, which takes the entire site build down rather than one page.
    /// </summary>
    [Fact]
    public void A_title_with_yaml_punctuation_is_quoted_and_escaped()
    {
        var front = GitRepoLayout.ForPreset("hugo").FrontMatter(
            "Reveal 2.0: the \"real\" story", null, Date, "s",
            draft: false, heroImage: null, tags: []);

        Assert.Contains("title: \"Reveal 2.0: the \\\"real\\\" story\"", front, StringComparison.Ordinal);
    }

    [Fact]
    public void Jekyll_carries_its_layout_field_and_uses_excerpt_for_the_description()
    {
        var front = GitRepoLayout.ForPreset("jekyll").FrontMatter(
            "How we ship", "The teaser", Date, "how-we-ship",
            draft: false, heroImage: null, tags: []);

        Assert.Contains("layout: \"post\"", front, StringComparison.Ordinal);
        Assert.Contains("excerpt: \"The teaser\"", front, StringComparison.Ordinal);
        Assert.DoesNotContain("description:", front, StringComparison.Ordinal);
    }

    [Fact]
    public void The_front_matter_block_is_delimited_the_way_every_generator_expects()
    {
        var front = GitRepoLayout.ForPreset("hugo").FrontMatter(
            "t", null, Date, "s", draft: false, heroImage: null, tags: []);

        Assert.StartsWith("---\n", front, StringComparison.Ordinal);
        Assert.EndsWith("---\n\n", front, StringComparison.Ordinal);
        // No BOM and no \r\n: Hugo and Jekyll fail to see front matter preceded by a BOM, and
        // \r\n fills the pull request diff with ^M.
        Assert.DoesNotContain("\r", front, StringComparison.Ordinal);
        Assert.DoesNotContain("﻿", front, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_layout_overrides_the_preset_and_a_broken_one_falls_back()
    {
        var stored = GitRepoLayout.Parse(
            """{"contentPath":"posts","contentFileTemplate":"{slug}/index.md"}""", "hugo");
        Assert.Equal("posts/how-we-ship/index.md", stored.ContentFilePath("how-we-ship", Date));

        // Unreadable layout JSON must not take publishing down with it.
        var broken = GitRepoLayout.Parse("{not json", "jekyll");
        Assert.Equal("_posts/2026-08-07-how-we-ship.md", broken.ContentFilePath("how-we-ship", Date));
    }
}
