using System.Text;
using System.Text.Json;
using System.IO.Compression;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Castmill.Api.Data;
using Castmill.Api.Endpoints;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Tenancy;
using Castmill.Core;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace Castmill.Api.Services.Evidence;

public sealed class SourceImportException(string message) : Exception(message);

public sealed record SourceImportResult(
    SourceAsset Source,
    IReadOnlyList<EvidenceBlock> Blocks,
    bool Created);

internal sealed record SourceBlockDraft(
    string StableId,
    string Content,
    string LocatorKind,
    string LocatorJson);

internal sealed record WebPageExtraction(
    string? Title,
    string? CanonicalUrl,
    bool HasReadableBody,
    bool IsJavaScriptShell,
    IReadOnlyList<SourceBlockDraft> Blocks);

internal sealed record StructuredWebFact(string SchemaType, string Field, string Value);

internal sealed record EligibleWebImage(string Url, string? Alt, int? Width, int? Height);

public interface ISourceImportService
{
    Task<SourceImportResult> ImportWebPageAsync(
        Guid campaignId, string url, string? label, CancellationToken ct);
    Task<SourceImportResult> ImportDocumentAsync(
        Guid campaignId, Guid assetId, string? label, CancellationToken ct);
    Task<SourceImportResult> ImportArtifactAsync(
        Guid campaignId, Guid artifactId, Guid? revisionId, string? label, CancellationToken ct);
}

public sealed class SourceImportService(
    IHttpClientFactory httpClientFactory,
    IBlobSasService blobs,
    CastmillDbContext db,
    ITenantProvider tenant,
    TimeProvider clock) : ISourceImportService
{
    private const int MaxRedirects = 5;
    private const int MaxWebBytes = 2 * 1024 * 1024;
    private const int MaxDocumentBytes = 20 * 1024 * 1024;
    private const int MaxArchiveEntries = 2_000;
    private const long MaxExpandedBytes = 64L * 1024 * 1024;
    private const int MaxPdfPages = 500;
    private const int MaxEvidenceBlocks = 1_000;
    private const int MaxBlockCharacters = 20_000;
    private const int MaxExtractedCharacters = 400_000;
    private static readonly TimeSpan ParserTimeout = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim ParserSlots = new(2, 2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SourceImportResult> ImportWebPageAsync(
        Guid campaignId, string url, string? label, CancellationToken ct)
    {
        await RequireCampaignAsync(campaignId, ct);
        var target = await PublicUrlGuard.ValidateAsync(url, ct);
        using var client = httpClientFactory.CreateClient("source-import");
        HttpResponseMessage? response = null;
        try
        {
            for (var redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                response?.Dispose();
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.TryAddWithoutValidation("User-Agent", "Castmill-SourceImport/1.0");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == MaxRedirects || response.Headers.Location is null)
                    {
                        throw new SourceImportException("The page redirected too many times.");
                    }
                    var next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(target, response.Headers.Location);
                    target = await PublicUrlGuard.ValidateAsync(next.ToString(), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SourceImportException(
                        $"{target.Host} returned {(int)response.StatusCode}.");
                }
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not ("text/html" or "application/xhtml+xml"))
                {
                    throw new SourceImportException(
                        "Only HTML pages can be imported from a URL.");
                }

                var bytes = await ReadCappedAsync(response.Content, MaxWebBytes, ct);
                var html = Encoding.UTF8.GetString(bytes);
                var extracted = await RunParserAsync(
                    () => ExtractWebPage(html, target), ct);
                if (!extracted.HasReadableBody)
                {
                    throw new SourceImportException(
                        extracted.IsJavaScriptShell
                            ? "This page renders its content with JavaScript. Castmill captured the server HTML but found no readable text; import a server-rendered page or paste its content instead."
                            : "The page contains no readable server-rendered content.");
                }
                ValidateExtracted(extracted.Blocks);
                return await PersistAsync(
                    campaignId,
                    SourceKinds.WebPage,
                    SourceModalities.Web,
                    label ?? extracted.Title ?? target.Host,
                    target.ToString(),
                    blobPath: null,
                    mediaType,
                    bytes.LongLength,
                    target.ToString(),
                    bytes,
                    extracted.Blocks,
                    ct);
            }
        }
        finally
        {
            response?.Dispose();
        }
        throw new SourceImportException("The page could not be imported.");
    }

    public async Task<SourceImportResult> ImportDocumentAsync(
        Guid campaignId, Guid assetId, string? label, CancellationToken ct)
    {
        await RequireCampaignAsync(campaignId, ct);
        var asset = await db.Assets.SingleOrDefaultAsync(candidate => candidate.Id == assetId, ct)
            ?? throw new SourceImportException("The uploaded document was not found.");
        if (asset.SizeBytes > MaxDocumentBytes)
        {
            throw new SourceImportException("Documents must be 20 MB or smaller.");
        }

        var opened = await blobs.OpenReadAsync(asset.BlobPath, ct)
            ?? throw new SourceImportException("The uploaded document has no stored content.");
        await using var source = opened.Stream;
        var bytes = await ReadCappedAsync(source, MaxDocumentBytes, ct);
        IReadOnlyList<SourceBlockDraft> blocks;
        try
        {
            blocks = await RunParserAsync(
                () => ExtractDocument(bytes, asset.ContentType, asset.FileName), ct);
        }
        catch (Exception ex) when (ex is not SourceImportException
            and not OperationCanceledException)
        {
            throw new SourceImportException(
                $"The document could not be read ({ex.GetType().Name}).");
        }
        if (blocks.Count == 0)
        {
            throw new SourceImportException("The document contains no readable text.");
        }
        ValidateExtracted(blocks);

        return await PersistAsync(
            campaignId,
            SourceKinds.Document,
            SourceModalities.Document,
            label ?? asset.FileName,
            originalUri: null,
            asset.BlobPath,
            asset.ContentType,
            bytes.LongLength,
            $"asset:{asset.Id:N}",
            bytes,
            blocks,
            ct);
    }

    public async Task<SourceImportResult> ImportArtifactAsync(
        Guid campaignId, Guid artifactId, Guid? revisionId, string? label, CancellationToken ct)
    {
        await RequireCampaignAsync(campaignId, ct);
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            candidate => candidate.Id == artifactId && candidate.CampaignId == campaignId,
            ct) ?? throw new SourceImportException("The Castmill artifact was not found.");
        if (artifact.Kind == "transcript")
        {
            throw new SourceImportException(
                "Transcript artifacts already have a source evidence projection.");
        }

        string contentJson;
        string title;
        long version;
        if (revisionId is { } historicalId)
        {
            var revision = await db.ArtifactRevisions.SingleOrDefaultAsync(
                candidate => candidate.Id == historicalId && candidate.ArtifactId == artifact.Id,
                ct) ?? throw new SourceImportException("The artifact revision was not found.");
            contentJson = revision.ContentJson;
            title = revision.Title;
            version = revision.Version;
        }
        else
        {
            contentJson = artifact.ContentJson;
            title = artifact.Title;
            version = artifact.Version;
        }

        var bytes = Encoding.UTF8.GetBytes(contentJson);
        if (bytes.Length > MaxDocumentBytes)
        {
            throw new SourceImportException("Artifact snapshots must be 20 MB or smaller.");
        }
        var blocks = await RunParserAsync(
            () => ExtractArtifact(contentJson, artifact.Id, revisionId, version), ct);
        if (blocks.Count == 0)
        {
            throw new SourceImportException("The artifact contains no readable text.");
        }
        ValidateExtracted(blocks);
        return await PersistAsync(
            campaignId,
            SourceKinds.CastmillArtifact,
            SourceModalities.Artifact,
            label ?? title,
            $"/campaigns/{campaignId}/focus?artifact={artifact.Id}",
            blobPath: null,
            "application/json",
            bytes.LongLength,
            revisionId is { } selectedRevision
                ? $"artifact:{artifact.Id:N}:revision:{selectedRevision:N}"
                : $"artifact:{artifact.Id:N}:version:{version}",
            bytes,
            blocks,
            ct);
    }

    internal static WebPageExtraction ExtractWebPage(
        string html, Uri url)
    {
        var document = new HtmlParser().ParseDocument(html);
        var structuredFacts = ExtractStructuredFacts(document);
        var scriptCharacters = document.QuerySelectorAll("script")
            .Sum(script => (long)script.TextContent.Length);
        var hasApplicationRoot = document.QuerySelector(
            "#app,#root,[data-reactroot],[data-v-app],[ng-version]") is not null;

        var title = FirstText(
            document.QuerySelector("meta[property='og:title']")?.GetAttribute("content"),
            document.Title,
            document.QuerySelector("h1")?.TextContent,
            structuredFacts.FirstOrDefault(fact => fact.Field is "headline" or "name")?.Value);
        var canonicalUrl = ResolvePageUrl(
            FirstText(
                document.QuerySelector("link[rel~='canonical']")?.GetAttribute("href"),
                document.QuerySelector("meta[property='og:url']")?.GetAttribute("content")),
            url);
        var author = FirstText(
            document.QuerySelector("meta[name='author']")?.GetAttribute("content"),
            document.QuerySelector("meta[property='article:author']")?.GetAttribute("content"),
            structuredFacts.FirstOrDefault(fact => fact.Field == "author")?.Value);
        var published = FirstText(
            document.QuerySelector("meta[property='article:published_time']")?.GetAttribute("content"),
            document.QuerySelector("meta[name='date']")?.GetAttribute("content"),
            structuredFacts.FirstOrDefault(fact => fact.Field == "datePublished")?.Value);
        var modified = FirstText(
            document.QuerySelector("meta[property='article:modified_time']")?.GetAttribute("content"),
            structuredFacts.FirstOrDefault(fact => fact.Field == "dateModified")?.Value);

        foreach (var element in document.QuerySelectorAll(
            "script,style,noscript,nav,header,footer,aside,form,dialog,svg,canvas,template,"
            + "[role='navigation'],[role='banner'],[role='contentinfo'],[aria-hidden='true'],"
            + "[class*='cookie' i],[id*='cookie' i],[class*='consent' i],[id*='consent' i],"
            + "[class*='sidebar' i],[id*='sidebar' i],[class*='breadcrumb' i],[id*='breadcrumb' i]"))
        {
            element.Remove();
        }

        var blocks = new List<SourceBlockDraft>();
        AddMetadataBlock(blocks, "title", "Title", title, url);
        AddMetadataBlock(blocks, "canonical", "Canonical URL", canonicalUrl, url);
        AddMetadataBlock(blocks, "author", "Author", author, url);
        AddMetadataBlock(blocks, "published", "Published", published, url);
        AddMetadataBlock(blocks, "modified", "Updated", modified, url);
        var factOrdinal = 0;
        foreach (var fact in structuredFacts
            .Where(fact => !IsDuplicateMetadataFact(fact, title, author, published, modified))
            .DistinctBy(fact => (fact.SchemaType, fact.Field, fact.Value))
            .Take(50))
        {
            factOrdinal++;
            blocks.Add(new SourceBlockDraft(
                $"structured-{factOrdinal:D4}",
                $"{StructuredFieldLabel(fact.Field)}: {fact.Value}",
                EvidenceLocatorKinds.WebPageMetadata,
                JsonSerializer.Serialize(new
                {
                    url = url.ToString(),
                    schemaType = fact.SchemaType,
                    field = fact.Field,
                    label = StructuredFieldLabel(fact.Field),
                }, Json)));
        }

        var contentRoot = document.QuerySelector("article")
            ?? document.QuerySelector("main")
            ?? document.Body;
        foreach (var image in ExtractEligibleImages(document, contentRoot, url, title, structuredFacts))
        {
            var imageOrdinal = blocks.Count(block => block.LocatorKind == EvidenceLocatorKinds.WebPageImage) + 1;
            blocks.Add(new SourceBlockDraft(
                $"image-{imageOrdinal:D4}",
                $"Eligible image: {image.Alt ?? "Image from the captured page"}",
                EvidenceLocatorKinds.WebPageImage,
                JsonSerializer.Serialize(new
                {
                    url = image.Url,
                    alt = image.Alt,
                    width = image.Width,
                    height = image.Height,
                }, Json)));
        }

        var bodyStart = blocks.Count;
        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        string? heading = null;
        var ordinal = 0;
        foreach (var element in contentRoot is null
            ? Enumerable.Empty<IElement>()
            : contentRoot.QuerySelectorAll("h1,h2,h3,h4,h5,h6,p,li,blockquote"))
        {
            var content = NormalizeText(element.TextContent);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }
            if (element.LocalName.StartsWith('h'))
            {
                heading = content;
            }
            if (content.Length < 20 && element.LocalName is not ("h1" or "h2" or "h3"))
            {
                continue;
            }
            if (!seenContent.Add(content))
            {
                continue;
            }

            ordinal++;
            blocks.Add(new SourceBlockDraft(
                $"web-{ordinal:D4}",
                content,
                EvidenceLocatorKinds.WebPageSection,
                JsonSerializer.Serialize(new
                {
                    url = url.ToString(),
                    heading,
                    element = element.LocalName,
                    ordinal,
                }, Json)));
        }
        var hasReadableBody = blocks.Count > bodyStart;
        return new WebPageExtraction(
            title,
            canonicalUrl,
            hasReadableBody,
            !hasReadableBody && (hasApplicationRoot || scriptCharacters >= 200),
            blocks);
    }

    private static List<StructuredWebFact> ExtractStructuredFacts(IDocument document)
    {
        var facts = new List<StructuredWebFact>();
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32,
                });
                CollectStructuredFacts(json.RootElement, facts);
            }
            catch (JsonException)
            {
                // Invalid publisher metadata is ignored; the readable page remains importable.
            }
        }
        return facts;
    }

    private static void CollectStructuredFacts(JsonElement element, List<StructuredWebFact> facts)
    {
        if (facts.Count >= 100)
        {
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectStructuredFacts(item, facts);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("@graph", out var graph))
        {
            CollectStructuredFacts(graph, facts);
        }
        var schemaType = JsonLdText(element, "@type") ?? "StructuredData";
        foreach (var field in new[]
        {
            "headline", "name", "description", "author", "datePublished",
            "dateModified", "sku", "category", "brand",
        })
        {
            if (JsonLdText(element, field) is { Length: > 0 } value)
            {
                facts.Add(new StructuredWebFact(schemaType, field, value));
            }
        }
        if (element.TryGetProperty("image", out var image))
        {
            foreach (var imageUrl in JsonLdUrls(image))
            {
                facts.Add(new StructuredWebFact(schemaType, "image", imageUrl));
            }
        }
        if (element.TryGetProperty("offers", out var offers))
        {
            var offerItems = offers.ValueKind == JsonValueKind.Array
                ? offers.EnumerateArray().ToArray()
                : [offers];
            foreach (var offer in offerItems)
            {
                foreach (var field in new[] { "price", "priceCurrency", "availability" })
                {
                    if (JsonLdText(offer, field) is { Length: > 0 } value)
                    {
                        facts.Add(new StructuredWebFact(schemaType, $"offers.{field}", value));
                    }
                }
            }
        }
    }

    private static string? JsonLdText(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeText(value.GetString() ?? string.Empty),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Object when value.TryGetProperty("name", out var name) =>
                JsonLdTextValue(name),
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray()
                .Select(JsonLdTextValue)
                .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => null,
        };
    }

    private static string? JsonLdTextValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => NormalizeText(value.GetString() ?? string.Empty),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Object when value.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String => NormalizeText(name.GetString() ?? string.Empty),
        _ => null,
    };

    private static IEnumerable<string> JsonLdUrls(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var url in JsonLdUrls(item))
                {
                    yield return url;
                }
            }
            yield break;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var url = NormalizeText(value.GetString() ?? string.Empty);
            if (url.Length > 0)
            {
                yield return url;
            }
            yield break;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in new[] { "url", "contentUrl" })
            {
                if (JsonLdText(value, field) is { Length: > 0 } url)
                {
                    yield return url;
                }
            }
        }
    }

    private static List<EligibleWebImage> ExtractEligibleImages(
        IDocument document,
        IElement? contentRoot,
        Uri pageUrl,
        string? title,
        IReadOnlyList<StructuredWebFact> structuredFacts)
    {
        var candidates = new List<(string? Source, string? Alt, int? Width, int? Height)>();
        candidates.Add((
            document.QuerySelector("meta[property='og:image']")?.GetAttribute("content"),
            document.QuerySelector("meta[property='og:image:alt']")?.GetAttribute("content") ?? title,
            ParseDimension(document.QuerySelector("meta[property='og:image:width']")?.GetAttribute("content")),
            ParseDimension(document.QuerySelector("meta[property='og:image:height']")?.GetAttribute("content"))));
        candidates.AddRange(structuredFacts
            .Where(fact => fact.Field == "image")
            .Select(fact => ((string?)fact.Value, title, (int?)null, (int?)null)));
        if (contentRoot is not null)
        {
            candidates.AddRange(contentRoot.QuerySelectorAll("img").Select(image => (
                image.GetAttribute("src") ?? image.GetAttribute("data-src"),
                image.GetAttribute("alt"),
                ParseDimension(image.GetAttribute("width")),
                ParseDimension(image.GetAttribute("height")))));
        }

        var images = new List<EligibleWebImage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var imageUrl = ResolvePageUrl(candidate.Source, pageUrl);
            var alt = FirstText(candidate.Alt);
            var tooSmall = candidate.Width is { } width && width < 200
                || candidate.Height is { } height && height < 100;
            if (imageUrl is null || tooSmall || (alt is null && candidate.Width is null && candidate.Height is null)
                || !seen.Add(imageUrl))
            {
                continue;
            }
            images.Add(new EligibleWebImage(imageUrl, alt, candidate.Width, candidate.Height));
            if (images.Count == 20)
            {
                break;
            }
        }
        return images;
    }

    private static void AddMetadataBlock(
        List<SourceBlockDraft> blocks, string id, string label, string? value, Uri sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        blocks.Add(new SourceBlockDraft(
            $"metadata-{id}",
            $"{label}: {value}",
            EvidenceLocatorKinds.WebPageMetadata,
            JsonSerializer.Serialize(new
            {
                url = sourceUrl.ToString(),
                field = id,
                label,
            }, Json)));
    }

    private static bool IsDuplicateMetadataFact(
        StructuredWebFact fact, string? title, string? author, string? published, string? modified) =>
        fact.Field switch
        {
            "headline" or "name" => string.Equals(fact.Value, title, StringComparison.OrdinalIgnoreCase),
            "author" => string.Equals(fact.Value, author, StringComparison.OrdinalIgnoreCase),
            "datePublished" => string.Equals(fact.Value, published, StringComparison.OrdinalIgnoreCase),
            "dateModified" => string.Equals(fact.Value, modified, StringComparison.OrdinalIgnoreCase),
            "image" => true,
            _ => false,
        };

    private static string StructuredFieldLabel(string field) => field switch
    {
        "headline" => "Headline",
        "name" => "Name",
        "description" => "Description",
        "author" => "Author",
        "datePublished" => "Published",
        "dateModified" => "Updated",
        "sku" => "SKU",
        "category" => "Category",
        "brand" => "Brand",
        "offers.price" => "Price",
        "offers.priceCurrency" => "Price currency",
        "offers.availability" => "Availability",
        _ => field,
    };

    private static string? FirstText(params string?[] values) => values
        .Select(value => NormalizeText(value ?? string.Empty))
        .FirstOrDefault(value => value.Length > 0);

    private static string? ResolvePageUrl(string? value, Uri pageUrl)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(pageUrl, value.Trim(), out var resolved)
            || resolved.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(resolved.UserInfo)
            || resolved.AbsoluteUri.Length > 2000)
        {
            return null;
        }
        return resolved.AbsoluteUri;
    }

    private static int? ParseDimension(string? value) =>
        int.TryParse(value, out var dimension) && dimension > 0 ? dimension : null;

    internal static IReadOnlyList<SourceBlockDraft> ExtractDocument(
        byte[] bytes, string contentType, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || extension is ".txt" or ".md" or ".markdown" or ".csv")
        {
            return BlocksFromText(Encoding.UTF8.GetString(bytes), "document");
        }
        if (contentType is "text/html" or "application/xhtml+xml" || extension is ".html" or ".htm")
        {
            return ExtractWebPage(Encoding.UTF8.GetString(bytes), new Uri("https://snapshot.invalid/"))
                .Blocks
                .Select((block, index) => block with
                {
                    StableId = $"doc-{index + 1:D4}",
                    LocatorKind = EvidenceLocatorKinds.DocumentSection,
                })
                .ToList();
        }
        if (contentType == "application/pdf" || extension == ".pdf")
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages > MaxPdfPages)
            {
                throw new SourceImportException(
                    $"PDF documents may contain at most {MaxPdfPages} pages.");
            }
            var blocks = new List<SourceBlockDraft>();
            var totalCharacters = 0;
            foreach (var page in pdf.GetPages())
            {
                var text = NormalizeText(page.Text);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                if (text.Length > MaxBlockCharacters)
                {
                    throw new SourceImportException(
                        $"PDF page {page.Number} exceeds the {MaxBlockCharacters:N0}-character block limit.");
                }
                totalCharacters = checked(totalCharacters + text.Length);
                if (totalCharacters > MaxExtractedCharacters)
                {
                    throw new SourceImportException(
                        $"Extracted evidence may contain at most {MaxExtractedCharacters:N0} characters.");
                }
                blocks.Add(new SourceBlockDraft(
                    $"page-{page.Number:D4}",
                    text,
                    EvidenceLocatorKinds.DocumentSection,
                    JsonSerializer.Serialize(new { page = page.Number }, Json)));
            }
            return blocks;
        }
        if (contentType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            || extension == ".docx")
        {
            ValidateOpenXmlArchive(bytes);
            using var stream = new MemoryStream(bytes, writable: false);
            using var word = WordprocessingDocument.Open(stream, false);
            var body = word.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return [];
            }
            var paragraphs = body
                .Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>();
            return paragraphs
                .Select((paragraph, index) => new
                {
                    Index = index + 1,
                    Text = NormalizeText(paragraph.InnerText),
                    Style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value,
                })
                .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
                .Select(paragraph => new SourceBlockDraft(
                    $"paragraph-{paragraph.Index:D4}",
                    paragraph.Text,
                    EvidenceLocatorKinds.DocumentSection,
                    JsonSerializer.Serialize(new
                    {
                        paragraph = paragraph.Index,
                        heading = paragraph.Style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true,
                    }, Json)))
                .ToList();
        }
        if (contentType == "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            || extension == ".pptx")
        {
            ValidateOpenXmlArchive(bytes);
            using var stream = new MemoryStream(bytes, writable: false);
            using var presentation = PresentationDocument.Open(stream, false);
            var presentationPart = presentation.PresentationPart;
            if (presentationPart is null)
            {
                return [];
            }
            return presentationPart.SlideParts
                .Select((slide, index) =>
                {
                    var slideRoot = slide.Slide;
                    return new
                    {
                        Number = index + 1,
                        Text = slideRoot is null
                            ? string.Empty
                            : NormalizeText(string.Join(" ", slideRoot
                                .Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                                .Select(text => text.Text))),
                    };
                })
                .Where(slide => !string.IsNullOrWhiteSpace(slide.Text))
                .Select(slide => new SourceBlockDraft(
                    $"slide-{slide.Number:D4}",
                    slide.Text,
                    EvidenceLocatorKinds.Slide,
                    JsonSerializer.Serialize(new { slide = slide.Number }, Json)))
                .ToList() ?? [];
        }

        throw new SourceImportException(
            "Supported documents are TXT, Markdown, HTML, PDF, DOCX, and PPTX.");
    }

    internal static IReadOnlyList<SourceBlockDraft> ExtractArtifact(
        string contentJson, Guid artifactId, Guid? revisionId, long version)
    {
        using var document = JsonDocument.Parse(contentJson);
        var root = document.RootElement.TryGetProperty("content", out var content)
            ? content
            : document.RootElement;
        var blocks = new List<SourceBlockDraft>();
        CollectText(root, "$", blocks, artifactId, revisionId, version);
        return blocks;
    }

    private async Task<SourceImportResult> PersistAsync(
        Guid campaignId,
        string kind,
        string modality,
        string label,
        string? originalUri,
        string? blobPath,
        string? contentType,
        long sizeBytes,
        string originIdentity,
        byte[] snapshot,
        IReadOnlyList<SourceBlockDraft> drafts,
        CancellationToken ct)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("Source import requires a tenant.");
        var snapshotHash = EvidenceRevisionHasher.HashContent(Convert.ToBase64String(snapshot));
        var snapshotIdentityHash = EvidenceRevisionHasher.HashContent(
            $"{kind}\n{originIdentity}\n{snapshotHash}");
        var snapshotIdentity = $"sha256:{snapshotIdentityHash}";
        var existing = await db.SourceAssets.SingleOrDefaultAsync(
            source => source.CampaignId == campaignId
                && source.Kind == kind
                && source.SnapshotIdentity == snapshotIdentity,
            ct);
        if (existing is not null)
        {
            var existingBlocks = await db.EvidenceBlocks
                .Where(block => block.SourceAssetId == existing.Id
                    && block.Revision == existing.CurrentEvidenceRevision)
                .OrderBy(block => block.Ordinal)
                .ToListAsync(ct);
            return new SourceImportResult(existing, existingBlocks, false);
        }

        var now = clock.GetUtcNow();
        var sourceId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var blocks = drafts.Select((draft, ordinal) => new EvidenceBlock
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CampaignId = campaignId,
            SourceAssetId = sourceId,
            StableId = draft.StableId,
            Ordinal = ordinal,
            Content = draft.Content,
            ContentHash = EvidenceRevisionHasher.HashContent(draft.Content),
            LocatorKind = draft.LocatorKind,
            LocatorJson = draft.LocatorJson,
            Revision = 1,
            RevisionId = revisionId,
            ApprovalState = EvidenceApprovalStates.Approved,
            IsExcluded = false,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
        var source = new SourceAsset
        {
            Id = sourceId,
            TenantId = tenantId,
            CampaignId = campaignId,
            Kind = kind,
            Modality = modality,
            Label = NormalizeLabel(label),
            OriginalUri = originalUri,
            BlobPath = blobPath,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            SnapshotIdentity = snapshotIdentity,
            SnapshotHash = snapshotHash,
            CurrentEvidenceRevision = 1,
            CurrentEvidenceRevisionId = revisionId,
            ApprovedEvidenceRevision = 1,
            ApprovedEvidenceRevisionId = revisionId,
            ApprovedEvidenceHash = EvidenceRevisionHasher.HashApproved(blocks),
            ApprovedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SourceAssets.Add(source);
        db.EvidenceBlocks.AddRange(blocks);
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await CampaignEndpoints.MarkLatestReportStaleAsync(
                    campaignId, db, now, inputs: true, ct: ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            });
        }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex))
        {
            db.ChangeTracker.Clear();
            var winner = await db.SourceAssets.SingleAsync(
                candidate => candidate.CampaignId == campaignId
                    && candidate.Kind == kind
                    && candidate.SnapshotIdentity == snapshotIdentity,
                ct);
            var winnerBlocks = await db.EvidenceBlocks
                .Where(block => block.SourceAssetId == winner.Id
                    && block.Revision == winner.CurrentEvidenceRevision)
                .OrderBy(block => block.Ordinal)
                .ToListAsync(ct);
            return new SourceImportResult(winner, winnerBlocks, false);
        }
        return new SourceImportResult(source, blocks, true);
    }

    private async Task RequireCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(campaign => campaign.Id == campaignId, ct))
        {
            throw new SourceImportException("The campaign was not found.");
        }
    }

    private static string NormalizeLabel(string label)
    {
        var normalized = NormalizeText(label);
        if (normalized.Length == 0)
        {
            return "Imported source";
        }
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private static bool IsUniqueConflict(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.SqlClient.SqlException
        {
            Number: 2601 or 2627,
        };

    private static async Task<byte[]> ReadCappedAsync(
        HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maxBytes)
        {
            throw new SourceImportException($"Source content exceeds the {maxBytes / 1024 / 1024} MB limit.");
        }
        await using var stream = await content.ReadAsStreamAsync(ct);
        return await ReadCappedAsync(stream, maxBytes, ct);
    }

    private static async Task<byte[]> ReadCappedAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            if (memory.Length + read > maxBytes)
            {
                throw new SourceImportException($"Source content exceeds the {maxBytes / 1024 / 1024} MB limit.");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return memory.ToArray();
    }

    private static async Task<T> RunParserAsync<T>(Func<T> parse, CancellationToken ct)
    {
        if (!await ParserSlots.WaitAsync(ParserTimeout, ct))
        {
            throw new SourceImportException("Source parsing is busy; try again shortly.");
        }
        var parser = Task.Run(parse, CancellationToken.None);
        try
        {
            return await parser.WaitAsync(ParserTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new SourceImportException("Source parsing exceeded the 15-second limit.");
        }
        finally
        {
            if (parser.IsCompleted)
            {
                _ = parser.Exception;
                ParserSlots.Release();
            }
            else
            {
                _ = parser.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        ParserSlots.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.Moved
            or System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.RedirectMethod
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private static void ValidateOpenXmlArchive(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new SourceImportException(
                $"Office documents may contain at most {MaxArchiveEntries:N0} archive entries.");
        }

        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
            {
                throw new SourceImportException(
                    "The expanded Office document exceeds the 64 MB processing limit.");
            }
            if (entry.CompressedLength > 0
                && entry.Length / (double)entry.CompressedLength > 200)
            {
                throw new SourceImportException(
                    "The Office document contains an unsafe compression ratio.");
            }
        }
    }

    private static void ValidateExtracted(IReadOnlyList<SourceBlockDraft> blocks)
    {
        if (blocks.Count > MaxEvidenceBlocks)
        {
            throw new SourceImportException(
                $"A source may contain at most {MaxEvidenceBlocks:N0} evidence blocks.");
        }
        if (blocks.Any(block => block.Content.Length > MaxBlockCharacters))
        {
            throw new SourceImportException(
                $"An evidence block may contain at most {MaxBlockCharacters:N0} characters.");
        }
        var total = blocks.Sum(block => (long)block.Content.Length);
        if (total > MaxExtractedCharacters)
        {
            throw new SourceImportException(
                $"Extracted evidence may contain at most {MaxExtractedCharacters:N0} characters.");
        }
    }

    private static List<SourceBlockDraft> BlocksFromText(string text, string prefix)
    {
        var chunks = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeText)
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk))
            .ToList();
        if (chunks.Count == 1 && chunks[0].Length > 4000)
        {
            chunks = chunks[0]
                .Chunk(3500)
                .Select(chars => new string(chars))
                .ToList();
        }
        return chunks.Select((chunk, index) => new SourceBlockDraft(
            $"{prefix}-{index + 1:D4}",
            chunk,
            EvidenceLocatorKinds.DocumentSection,
            JsonSerializer.Serialize(new { section = index + 1 }, Json)))
            .ToList();
    }

    private static void CollectText(
        JsonElement element,
        string path,
        List<SourceBlockDraft> blocks,
        Guid artifactId,
        Guid? revisionId,
        long version)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("citations") || property.NameEquals("validation"))
                    {
                        continue;
                    }
                    CollectText(
                        property.Value,
                        $"{path}.{property.Name}",
                        blocks,
                        artifactId,
                        revisionId,
                        version);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectText(item, $"{path}[{index++}]", blocks, artifactId, revisionId, version);
                }
                break;
            case JsonValueKind.String:
                var value = NormalizeText(element.GetString() ?? string.Empty);
                if (value.Length == 0)
                {
                    return;
                }
                foreach (var chunk in BlocksFromText(value, "artifact"))
                {
                    var ordinal = blocks.Count + 1;
                    blocks.Add(chunk with
                    {
                        StableId = $"artifact-{ordinal:D4}",
                        LocatorKind = EvidenceLocatorKinds.ArtifactField,
                        LocatorJson = JsonSerializer.Serialize(new
                        {
                            artifactId,
                            revisionId,
                            version,
                            path,
                        }, Json),
                    });
                }
                break;
        }
    }

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
