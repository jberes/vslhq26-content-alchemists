using System.IO.Compression;
using System.Text;
using Castmill.Api.Services.Export;
using Castmill.Core;
using Castmill.Core.Content;

namespace Castmill.Api.Tests;

/// <summary>
/// Getting the work back out (roadmap 5.6). Export is where keeping the editor's contract to
/// markdown pays off — the body is already the format — but it also has to cope with the two
/// payload shapes that are live in the database and with structured kinds that have no
/// markdown body at all.
/// </summary>
public sealed class ExportTests
{
    private static readonly ExportService Export = new();

    private static Artifact Artifact(string kind, string title, string contentJson) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        CampaignId = Guid.NewGuid(),
        Kind = kind,
        Title = title,
        ContentJson = contentJson,
        Status = ArtifactStatus.Draft,
        Version = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void The_orchestrators_envelope_and_a_flat_payload_export_the_same_body()
    {
        var body = System.Text.Json.JsonSerializer.Serialize("# Launch\n\nWe cut deploy time in half.");

        var wrapped = Export.Markdown(Artifact("blog", "Launch",
            "{\"content\":{\"title\":\"Launch\",\"markdown\":" + body + "},\"validation\":{\"Passed\":true,\"Warnings\":[]}}"));
        var flat = Export.Markdown(Artifact("blog", "Launch",
            "{\"title\":\"Launch\",\"markdown\":" + body + "}"));

        Assert.Contains("We cut deploy time in half.", wrapped, StringComparison.Ordinal);
        Assert.Equal(flat, wrapped);
        // The bookkeeping envelope never leaks into the exported document.
        Assert.DoesNotContain("validation", wrapped, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Provenance travels with the content, or the export has lost the point of it.</summary>
    [Fact]
    public void Citations_are_appended_so_an_exported_artifact_keeps_its_provenance()
    {
        var markdown = Export.Markdown(Artifact("blog", "Launch",
            """{"content":{"title":"Launch","markdown":"Body.","citations":["S1","S4"]}}"""));

        Assert.Contains("Sources: S1, S4", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// A social post has no markdown body. Exporting raw JSON would be useless, so the
    /// fields are projected as headings — every field, including ones we did not anticipate.
    /// </summary>
    [Fact]
    public void A_structured_artifact_exports_its_fields_rather_than_raw_json()
    {
        var markdown = Export.Markdown(Artifact("social-linkedin", "Launch post",
            """{"content":{"title":"Launch post","text":"We halved deploy time.","hashtags":["devops","ci"],"citations":["S2"]}}"""));

        Assert.Contains("# Launch post", markdown, StringComparison.Ordinal);
        Assert.Contains("## Text", markdown, StringComparison.Ordinal);
        Assert.Contains("We halved deploy time.", markdown, StringComparison.Ordinal);
        Assert.Contains("## Hashtags", markdown, StringComparison.Ordinal);
        Assert.Contains("devops", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hashtags\"", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_docx_is_a_real_word_package_with_heading_styles()
    {
        var bytes = Export.Docx(Artifact("blog", "Launch",
            """{"content":{"title":"Launch","markdown":"# Launch\n\nBody text.\n\n## Detail\n\nMore."}}"""));

        // Opens as an OPC package with the parts Word expects, rather than as bold text.
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, e => e.FullName == "word/document.xml");
        Assert.Contains(archive.Entries, e => e.FullName == "word/styles.xml");

        var document = ReadEntry(archive, "word/document.xml");
        Assert.Contains("Heading1", document, StringComparison.Ordinal);
        Assert.Contains("Heading2", document, StringComparison.Ordinal);
        Assert.Contains("Body text.", document, StringComparison.Ordinal);
    }

    [Fact]
    public void A_campaign_archive_groups_by_kind_and_carries_an_index()
    {
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Webinar campaign",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var bytes = Export.Zip(campaign, [
            Artifact("blog", "The first blog", """{"content":{"markdown":"One."}}"""),
            Artifact("blog", "The second blog", """{"content":{"markdown":"Two."}}"""),
            Artifact("social-x", "Launch thread", """{"content":{"text":"Hi."}}"""),
        ]);

        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Contains(archive.Entries, e => e.FullName == "blog/the-first-blog.md");
        Assert.Contains(archive.Entries, e => e.FullName == "blog/the-second-blog.md");
        Assert.Contains(archive.Entries, e => e.FullName == "social-x/launch-thread.md");

        var index = ReadEntry(archive, "README.md");
        Assert.Contains("Webinar campaign", index, StringComparison.Ordinal);
        Assert.Contains("3 artifacts exported", index, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two artifacts of a kind can share a title — "add another blog" makes that likely — and
    /// a zip entry collision would silently drop one of them.
    /// </summary>
    [Fact]
    public void Two_artifacts_with_the_same_title_both_survive_the_archive()
    {
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Campaign",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var bytes = Export.Zip(campaign, [
            Artifact("blog", "Same title", """{"content":{"markdown":"One."}}"""),
            Artifact("blog", "Same title", """{"content":{"markdown":"Two."}}"""),
        ]);

        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var blogs = archive.Entries.Where(e => e.FullName.StartsWith("blog/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, blogs.Count);
        Assert.Contains("One.", ReadEntry(archive, blogs[0].FullName), StringComparison.Ordinal);
        Assert.Contains("Two.", ReadEntry(archive, blogs[1].FullName), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Deploy time, halved!", "deploy-time-halved")]
    [InlineData("  Spaced   out  ", "spaced-out")]
    [InlineData("///", "untitled")]
    [InlineData("", "untitled")]
    public void Titles_become_safe_file_names(string title, string expected)
    {
        Assert.Equal(expected, ExportService.Slug(title));
    }

    /// <summary>A model-written title cannot escape the archive directory.</summary>
    [Fact]
    public void A_path_traversal_title_cannot_escape_the_archive()
    {
        Assert.Equal("etc-passwd", ExportService.Slug("../../etc/passwd"));
    }

    [Fact]
    public void Field_names_read_as_headings_rather_than_as_json_keys()
    {
        Assert.Equal("Meta description", ArtifactMarkdown.Humanize("metaDescription"));
        Assert.Equal("Body markdown", ArtifactMarkdown.Humanize("bodyMarkdown"));
        Assert.Equal("Text", ArtifactMarkdown.Humanize("text"));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
