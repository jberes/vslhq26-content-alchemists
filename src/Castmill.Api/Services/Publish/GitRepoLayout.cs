using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Castmill.Api.Services.Publish;

/// <summary>
/// Where files go and what the front matter looks like. Held as JSON on the profile so a new
/// static-site preset costs no migration (ADR-003).
///
/// Every field here is load-bearing rather than cosmetic, because the generators genuinely
/// disagree: Jekyll parses the date and slug out of a mandatory <c>YYYY-MM-DD-</c> filename
/// prefix, Hugo leaf bundles want <c>{slug}/index.md</c>, and Astro validates front matter
/// with a strict schema so an unexpected key fails the site BUILD rather than one page.
/// </summary>
public sealed class GitRepoLayout
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Directory for content files, no trailing slash.</summary>
    public string ContentPath { get; set; } = "content/posts";

    /// <summary>Tokens: {slug}, {date}. e.g. "{date}-{slug}.md" or "{slug}/index.md".</summary>
    public string ContentFileTemplate { get; set; } = "{slug}.md";

    /// <summary>Where image BYTES are written.</summary>
    public string ImagePath { get; set; } = "static/img/{slug}";

    /// <summary>
    /// What the markdown SAYS, which is almost never where the bytes went — Hugo strips
    /// <c>static/</c>, Next serves <c>public/</c> from the root. Conflating the two is the
    /// single most common broken-image bug in this kind of integration.
    /// </summary>
    public string ImageReferencePrefix { get; set; } = "/img/{slug}";

    /// <summary>yyyy-MM-dd · RFC 3339 · Jekyll's space-separated form. A wrong one fails the build.</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>
    /// Our field name → theirs. A null or empty value OMITS the field entirely, which is
    /// what makes a strict-schema target like Astro publishable at all.
    /// </summary>
    public Dictionary<string, string?> FieldMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "title",
        ["description"] = "description",
        ["date"] = "date",
        ["slug"] = "slug",
        ["draft"] = "draft",
        ["heroImage"] = "featured_image",
    };

    /// <summary>Constant front-matter entries, e.g. Jekyll's <c>layout: post</c>.</summary>
    public Dictionary<string, string> ExtraFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>draft-true (Hugo/Jekyll) · published-false (several Next patterns) · omit.</summary>
    public string DraftSemantics { get; set; } = "draft-true";

    public static GitRepoLayout ForPreset(string preset) => preset.ToLowerInvariant() switch
    {
        "jekyll" => new GitRepoLayout
        {
            ContentPath = "_posts",
            // NOT optional: Jekyll reads the date and the slug out of the file name.
            ContentFileTemplate = "{date}-{slug}.md",
            ImagePath = "assets/img/{slug}",
            ImageReferencePrefix = "/assets/img/{slug}",
            DateFormat = "yyyy-MM-dd HH:mm:ss zzz",
            ExtraFields = new(StringComparer.OrdinalIgnoreCase) { ["layout"] = "post" },
            FieldMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "title",
                ["description"] = "excerpt",
                ["date"] = "date",
                ["draft"] = null,       // Jekyll uses the _drafts folder instead
                ["heroImage"] = "image",
            },
        },
        "astro" => new GitRepoLayout
        {
            ContentPath = "src/content/blog",
            ContentFileTemplate = "{slug}.md",
            // Astro's image() helper resolves paths relative to the entry, so bytes sit
            // beside the markdown rather than in a public directory.
            ImagePath = "src/content/blog/{slug}",
            ImageReferencePrefix = "./{slug}",
            DateFormat = "yyyy-MM-dd",
            FieldMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "title",
                ["description"] = "description",
                ["date"] = "pubDate",
                ["draft"] = "draft",
                ["heroImage"] = "heroImage",
                ["slug"] = null,        // strict schema: an unknown key fails the build
            },
        },
        "nextjs" => new GitRepoLayout
        {
            ContentPath = "content/blog",
            ContentFileTemplate = "{slug}.mdx",
            ImagePath = "public/images/{slug}",
            ImageReferencePrefix = "/images/{slug}",
            DateFormat = "yyyy-MM-dd",
            DraftSemantics = "published-false",
            FieldMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "title",
                ["description"] = "description",
                ["date"] = "date",
                ["slug"] = "slug",
                ["draft"] = "published",
                ["heroImage"] = "image",
            },
        },
        // Hugo is the default shape.
        _ => new GitRepoLayout { DateFormat = "yyyy-MM-ddTHH:mm:sszzz" },
    };

    public static GitRepoLayout Parse(string? json, string preset)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ForPreset(preset);
        }

        try
        {
            return JsonSerializer.Deserialize<GitRepoLayout>(json, Json) ?? ForPreset(preset);
        }
        catch (JsonException)
        {
            return ForPreset(preset);
        }
    }

    public string ContentFilePath(string slug, DateTimeOffset date) =>
        Combine(ContentPath, ContentFileTemplate
            .Replace("{slug}", slug, StringComparison.Ordinal)
            .Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal));

    public string ImageDirectory(string slug) =>
        ImagePath.Replace("{slug}", slug, StringComparison.Ordinal).Trim('/');

    public string ImageReference(string slug, string fileName) =>
        $"{ImageReferencePrefix.Replace("{slug}", slug, StringComparison.Ordinal).TrimEnd('/')}/{fileName}";

    /// <summary>
    /// The YAML block. Values are quoted and internal quotes escaped — a model-written title
    /// containing a colon or a quote would otherwise produce a YAML parse error and take the
    /// whole site build down with it.
    /// </summary>
    public string FrontMatter(
        string title, string? description, DateTimeOffset date, string slug,
        bool draft, string? heroImage, IReadOnlyList<string> tags)
    {
        var lines = new List<string>();

        void Add(string logical, string? value, bool quote = true)
        {
            if (!FieldMap.TryGetValue(logical, out var name) || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            lines.Add($"{name}: {(quote ? Quote(value) : value)}");
        }

        Add("title", title);
        Add("description", description);
        Add("date", date.ToString(DateFormat, CultureInfo.InvariantCulture));
        Add("slug", slug);
        Add("heroImage", heroImage);

        if (FieldMap.TryGetValue("draft", out var draftField) && !string.IsNullOrWhiteSpace(draftField))
        {
            // published-false is the same idea inverted: some templates gate on published.
            var value = DraftSemantics.Equals("published-false", StringComparison.OrdinalIgnoreCase)
                ? (!draft).ToString().ToLowerInvariant()
                : draft.ToString().ToLowerInvariant();
            lines.Add($"{draftField}: {value}");
        }

        if (tags.Count > 0 && FieldMap.TryGetValue("tags", out var tagField) && !string.IsNullOrWhiteSpace(tagField))
        {
            lines.Add($"{tagField}: [{string.Join(", ", tags.Select(Quote))}]");
        }

        foreach (var (key, value) in ExtraFields)
        {
            lines.Add($"{key}: {Quote(value)}");
        }

        var text = new StringBuilder("---\n");
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }
        text.Append("---\n\n");
        return text.ToString();
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string Combine(string directory, string file) =>
        string.IsNullOrWhiteSpace(directory) ? file : $"{directory.Trim('/')}/{file}";
}
