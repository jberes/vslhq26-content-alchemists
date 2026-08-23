using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Castmill.UI.Editor;

public sealed record BlogMetadataDocument(
    string Title,
    string Markdown,
    DateTimeOffset UpdatedAt,
    BlogPublishingMetadata Metadata);

public sealed record BlogMetadataOutput(
    string Combined,
    string HtmlHead,
    string JsonLdOnly);

public sealed record BlogFaq(string Question, string Answer);

public static partial class BlogMetadataBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Slug(string value)
    {
        var slug = new StringBuilder();
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var normalized = slug.ToString().Trim('-');
        return normalized.Length == 0 ? "untitled" : normalized;
    }

    public static string? BuildCanonicalUrl(string? siteUrl, string? slug, string title)
    {
        if (HttpUrl(siteUrl) is not { } site)
        {
            return null;
        }

        var builder = new UriBuilder(site)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var basePath = builder.Path.TrimEnd('/');
        builder.Path = $"{basePath}/{Slug(string.IsNullOrWhiteSpace(slug) ? title : slug)}";
        return builder.Uri.AbsoluteUri;
    }

    public static BlogMetadataOutput Build(BlogMetadataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var metadata = document.Metadata;
        var title = string.IsNullOrWhiteSpace(metadata.Title) ? document.Title : metadata.Title.Trim();
        var description = string.IsNullOrWhiteSpace(metadata.Description) ? title : metadata.Description.Trim();
        var canonical = HttpUrl(metadata.CanonicalUrl)?.AbsoluteUri
            ?? BuildCanonicalUrl(metadata.SiteUrl, metadata.Slug, title);
        var siteUrl = HttpUrl(metadata.SiteUrl)?.AbsoluteUri;
        var logoUrl = HttpUrl(metadata.OrganizationLogoUrl)?.AbsoluteUri;
        var videoUrl = HttpUrl(metadata.VideoUrl)?.AbsoluteUri;

        var head = BuildHead(title, description, canonical, metadata, siteUrl);
        var graph = new JsonArray();
        var article = new JsonObject
        {
            ["@type"] = "Article",
            ["headline"] = title,
            ["description"] = description,
            ["dateModified"] = document.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        Add(article, "url", canonical);
        Add(article, "mainEntityOfPage", canonical);
        Add(article, "keywords", metadata.Keywords);

        if (!string.IsNullOrWhiteSpace(metadata.Author))
        {
            article["author"] = new JsonObject
            {
                ["@type"] = "Person",
                ["name"] = metadata.Author.Trim(),
            };
        }

        var organizationName = string.IsNullOrWhiteSpace(metadata.OrganizationName)
            ? metadata.SiteName
            : metadata.OrganizationName;
        if (!string.IsNullOrWhiteSpace(organizationName) || logoUrl is not null)
        {
            var publisher = new JsonObject { ["@type"] = "Organization" };
            Add(publisher, "name", organizationName);
            if (logoUrl is not null)
            {
                publisher["logo"] = new JsonObject
                {
                    ["@type"] = "ImageObject",
                    ["url"] = logoUrl,
                };
            }
            article["publisher"] = publisher;
        }

        if (!string.IsNullOrWhiteSpace(metadata.SiteName))
        {
            var website = new JsonObject
            {
                ["@type"] = "WebSite",
                ["name"] = metadata.SiteName.Trim(),
            };
            Add(website, "url", siteUrl);
            article["isPartOf"] = website;
        }
        graph.Add(article);

        if (videoUrl is not null && VisibleUrl(document.Markdown, videoUrl))
        {
            graph.Add(new JsonObject
            {
                ["@type"] = "VideoObject",
                ["name"] = title,
                ["description"] = description,
                ["contentUrl"] = videoUrl,
            });
        }

        var faq = VisibleFaq(document.Markdown);
        if (faq.Count > 0)
        {
            var questions = new JsonArray();
            foreach (var entry in faq)
            {
                questions.Add(new JsonObject
                {
                    ["@type"] = "Question",
                    ["name"] = entry.Question,
                    ["acceptedAnswer"] = new JsonObject
                    {
                        ["@type"] = "Answer",
                        ["text"] = entry.Answer,
                    },
                });
            }
            graph.Add(new JsonObject
            {
                ["@type"] = "FAQPage",
                ["mainEntity"] = questions,
            });
        }

        var jsonLd = new JsonObject
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = graph,
        }.ToJsonString(JsonOptions);
        var script = $"<script type=\"application/ld+json\">\n{jsonLd}\n</script>";
        return new BlogMetadataOutput($"{head}\n{script}", head, jsonLd);
    }

    public static IReadOnlyList<BlogFaq> VisibleFaq(string markdown)
    {
        var entries = new List<BlogFaq>();
        var answer = new List<string>();
        var inFaq = false;
        var faqLevel = 0;
        string? question = null;

        void Flush()
        {
            var text = PlainText(string.Join(' ', answer));
            if (!string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(text))
            {
                entries.Add(new BlogFaq(question, text));
            }
            question = null;
            answer.Clear();
        }

        foreach (var rawLine in VisibleMarkdown(markdown).Split('\n'))
        {
            if (Heading(rawLine) is { } heading)
            {
                var faqHeading = heading.Text.Equals("FAQ", StringComparison.OrdinalIgnoreCase)
                    || heading.Text.Equals("Frequently Asked Questions", StringComparison.OrdinalIgnoreCase);
                if (!inFaq)
                {
                    if (faqHeading)
                    {
                        inFaq = true;
                        faqLevel = heading.Level;
                    }
                }
                else if (heading.Level <= faqLevel)
                {
                    Flush();
                    inFaq = faqHeading;
                    faqLevel = faqHeading ? heading.Level : 0;
                }
                else
                {
                    Flush();
                    var candidate = PlainText(heading.Text);
                    question = candidate.EndsWith('?') ? candidate : null;
                }
                continue;
            }

            if (inFaq && question is not null && !string.IsNullOrWhiteSpace(rawLine))
            {
                answer.Add(rawLine.Trim());
            }
        }
        Flush();
        return entries;
    }

    private static string BuildHead(
        string title,
        string description,
        string? canonical,
        BlogPublishingMetadata metadata,
        string? siteUrl)
    {
        var head = new StringBuilder();
        head.AppendLine(CultureInfo.InvariantCulture, $"<title>{Encode(title)}</title>");
        Meta(head, "name", "description", description);
        Meta(head, "name", "keywords", metadata.Keywords);
        Meta(head, "name", "author", metadata.Author);
        Meta(head, "property", "og:type", "article");
        Meta(head, "property", "og:title", title);
        Meta(head, "property", "og:description", description);
        Meta(head, "property", "og:site_name", metadata.SiteName);
        Meta(head, "property", "og:url", canonical);
        if (canonical is not null)
        {
            head.AppendLine(CultureInfo.InvariantCulture,
                $"<link rel=\"canonical\" href=\"{Encode(canonical)}\" />");
        }
        if (siteUrl is not null)
        {
            head.AppendLine(CultureInfo.InvariantCulture,
                $"<link rel=\"home\" href=\"{Encode(siteUrl)}\" />");
        }
        return head.ToString().TrimEnd();
    }

    private static void Meta(StringBuilder head, string attribute, string name, string? content)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            head.AppendLine(CultureInfo.InvariantCulture,
                $"<meta {attribute}=\"{Encode(name)}\" content=\"{Encode(content.Trim())}\" />");
        }
    }

    private static void Add(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value.Trim();
        }
    }

    private static Uri? HttpUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return uri;
    }

    private static bool VisibleUrl(string markdown, string url)
    {
        var visible = VisibleMarkdown(markdown);
        if (visible.Contains(url, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && visible.Contains(uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.Unescaped),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string VisibleMarkdown(string markdown)
    {
        var visible = new StringBuilder();
        var inFence = false;
        var inComment = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = rawLine.TrimStart();
            if (!inComment && (trimmed.StartsWith("```", StringComparison.Ordinal)
                               || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                continue;
            }

            var line = new StringBuilder();
            var offset = 0;
            while (offset < rawLine.Length)
            {
                if (inComment)
                {
                    var end = rawLine.IndexOf("-->", offset, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }
                    inComment = false;
                    offset = end + 3;
                }
                else
                {
                    var start = rawLine.IndexOf("<!--", offset, StringComparison.Ordinal);
                    if (start < 0)
                    {
                        line.Append(rawLine.AsSpan(offset));
                        break;
                    }
                    line.Append(rawLine.AsSpan(offset, start - offset));
                    inComment = true;
                    offset = start + 4;
                }
            }

            var renderedLine = line.ToString();
            if (!LinkDefinition().IsMatch(renderedLine))
            {
                visible.AppendLine(renderedLine);
            }
        }
        return visible.ToString();
    }

    private static (int Level, string Text)? Heading(string line)
    {
        var trimmed = line.TrimStart();
        var level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
        {
            level++;
        }
        return level > 0 && level < trimmed.Length && trimmed[level] == ' '
            ? (level, trimmed[(level + 1)..].Trim())
            : null;
    }

    private static string PlainText(string markdown)
    {
        var text = MarkdownLink().Replace(markdown, "$1");
        text = HtmlTag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    [GeneratedRegex(@"!?\[([^\]]+)\]\([^)]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^\s*\[[^\]]+\]:\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LinkDefinition();
}