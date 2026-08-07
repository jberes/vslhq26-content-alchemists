using System.Globalization;
using System.IO.Compression;
using System.Text;
using Castmill.Core;
using Castmill.Core.Content;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Castmill.Api.Services.Export;

public interface IExportService
{
    string Markdown(Artifact artifact);

    /// <summary>Styled .docx bytes — Word opens it with real heading styles, not bold text.</summary>
    byte[] Docx(Artifact artifact);

    /// <summary>Every artifact in the campaign as markdown files in one archive.</summary>
    byte[] Zip(Campaign campaign, IReadOnlyList<Artifact> artifacts);
}

/// <summary>
/// Roadmap 5.6. Export is the payoff for having kept the editor's contract to markdown: the
/// body is already the format, so .md is a copy and .docx is a formatting pass over it rather
/// than a conversion from some editor-native shape.
/// </summary>
public sealed class ExportService : IExportService
{
    public string Markdown(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var body = ArtifactMarkdown.ForExport(artifact.Kind, artifact.Title, artifact.ContentJson);
        var citations = ArtifactMarkdown.Citations(artifact.ContentJson);
        if (citations.Count == 0)
        {
            return body;
        }

        // Provenance travels with the content. An exported artifact that has lost which
        // transcript moments it came from is exactly the thing the citation contract exists
        // to prevent (G5).
        var text = new StringBuilder(body.TrimEnd());
        text.AppendLine().AppendLine().AppendLine("---").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Sources: {string.Join(", ", citations)}");
        return text.ToString();
    }

    public byte[] Docx(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body());
            AddStyles(main);

            var body = main.Document.Body!;
            foreach (var block in Blocks(Markdown(artifact)))
            {
                body.AppendChild(block);
            }
        }

        return stream.ToArray();
    }

    public byte[] Zip(Campaign campaign, IReadOnlyList<Artifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(artifacts);

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in artifacts)
            {
                // Grouped by kind so the archive reads like the Mill Floor rather than like a
                // database dump, and de-duplicated because a campaign can hold several blogs.
                var name = Unique(used, $"{artifact.Kind}/{Slug(artifact.Title)}.md");
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Markdown(artifact));
            }

            var index = archive.CreateEntry("README.md", CompressionLevel.Optimal);
            using var indexWriter = new StreamWriter(index.Open(), new UTF8Encoding(false));
            indexWriter.Write(Index(campaign, artifacts));
        }

        return stream.ToArray();
    }

    private static string Index(Campaign campaign, IReadOnlyList<Artifact> artifacts)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {campaign.Name}").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{artifacts.Count} artifact{(artifacts.Count == 1 ? "" : "s")} exported from Castmill.")
            .AppendLine();

        foreach (var group in artifacts.GroupBy(a => a.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}").AppendLine();
            foreach (var artifact in group.OrderBy(a => a.CreatedAt))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {artifact.Title} ({artifact.Status})");
            }
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// Markdown → Word blocks. Deliberately a small line-based pass rather than a full parser:
    /// the shapes that matter in an exported artifact are headings, paragraphs, list items and
    /// fenced code, and a half-supported full parser would be worse than an honest subset.
    /// </summary>
    private static IEnumerable<OpenXmlElement> Blocks(string markdown)
    {
        var inCode = false;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                yield return Paragraph(line, "CastmillCode");
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                yield return Paragraph(line[4..], "Heading3");
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                yield return Paragraph(line[3..], "Heading2");
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                yield return Paragraph(line[2..], "Heading1");
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                yield return Paragraph("• " + line[2..], "Normal");
            }
            else if (line.StartsWith("---", StringComparison.Ordinal))
            {
                yield return Paragraph(string.Empty, "Normal");
            }
            else
            {
                yield return Paragraph(line, "Normal");
            }
        }
    }

    private static Paragraph Paragraph(string text, string styleId) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    /// <summary>
    /// Serif headings and a monospace code style, so the .docx reads as a document rather
    /// than as pasted text (the acceptance on roadmap 5.6 is that Word opens it with correct
    /// heading styles).
    /// </summary>
    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        styles.Append(new Style(
            new StyleName { Val = "Normal" },
            new StyleRunProperties(new RunFonts { Ascii = "Georgia", HighAnsi = "Georgia" },
                new FontSize { Val = "22" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true,
        });

        foreach (var (id, size) in new[] { ("Heading1", "40"), ("Heading2", "30"), ("Heading3", "26") })
        {
            styles.Append(new Style(
                new StyleName { Val = id },
                new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" }),
                new StyleRunProperties(
                    new RunFonts { Ascii = "Georgia", HighAnsi = "Georgia" },
                    new Bold(),
                    new FontSize { Val = size }))
            {
                Type = StyleValues.Paragraph,
                StyleId = id,
            });
        }

        styles.Append(new Style(
            new StyleName { Val = "Castmill Code" },
            new BasedOn { Val = "Normal" },
            new StyleRunProperties(
                new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                new FontSize { Val = "20" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "CastmillCode",
        });

        part.Styles = styles;
    }

    /// <summary>A model-written title must not become an illegal or colliding archive entry.</summary>
    internal static string Slug(string title)
    {
        var slug = new StringBuilder();
        foreach (var c in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        if (text.Length == 0)
        {
            return "untitled";
        }
        return text.Length <= 60 ? text : text[..60].TrimEnd('-');
    }

    private static string Unique(HashSet<string> used, string name)
    {
        if (used.Add(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{name[..^3]}-{i}.md";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
