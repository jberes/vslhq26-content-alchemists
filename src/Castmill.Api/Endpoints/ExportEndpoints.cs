using Castmill.Api.Data;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Export;
using Castmill.Core.Content;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

/// <summary>
/// Roadmap 5.6 — getting the work back out. Downloads rather than JSON: the browser saves
/// these, and a base64 payload in a JSON envelope would only make the client re-decode what
/// the transport already knows how to stream.
/// </summary>
public static class ExportEndpoints
{
    private const int MaxImageCount = 100;
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private const int MaxTotalImageBytes = 100 * 1024 * 1024;

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns").RequireAuthorization("TenantAllowed");

        group.MapGet("/{campaignId:guid}/artifacts/{artifactId:guid}/export", ArtifactAsync);
        group.MapGet("/{campaignId:guid}/export", CampaignAsync);
        return routes;
    }

    private static async Task<IResult> ArtifactAsync(
        Guid campaignId,
        Guid artifactId,
        string? format,
        CastmillDbContext db,
        IExportService export,
        CancellationToken ct)
    {
        // Tenant-filtered by the global query filter, so another tenant's id is a 404.
        var artifact = await db.Artifacts
            .SingleOrDefaultAsync(a => a.Id == artifactId && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        var slug = ExportService.Slug(artifact.Title);

        return (format ?? "md").ToLowerInvariant() switch
        {
            "md" or "markdown" => Results.File(
                System.Text.Encoding.UTF8.GetBytes(export.Markdown(artifact)),
                "text/markdown; charset=utf-8", $"{slug}.md"),

            "docx" => Results.File(
                export.Docx(artifact),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                $"{slug}.docx"),

            _ => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Supported formats are 'md' and 'docx'."),
        };
    }

    private static async Task<IResult> CampaignAsync(
        Guid campaignId,
        CastmillDbContext db,
        IExportService export,
        IPublicContentStore publicStore,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        // The transcript is source material rather than output: it would dominate the archive
        // and it is not something anyone publishes.
        var artifacts = await db.Artifacts
            .Where(a => a.CampaignId == campaignId && a.Kind != "transcript")
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync(ct);

        var placed = await db.ImageSlots
            .Where(slot => slot.CampaignId == campaignId
                           && slot.State == "Filled"
                           && slot.PublishedUrl != null)
            .OrderBy(slot => slot.ArtifactId)
            .ThenBy(slot => slot.Kind)
            .ThenBy(slot => slot.Id)
            .Take(MaxImageCount + 1)
            .ToListAsync(ct);
        var variants = await db.ImageVariants
            .Where(variant => variant.CampaignId == campaignId && variant.State != "Discarded")
            .Join(db.ImageSlots.Where(slot => slot.CampaignId == campaignId),
                variant => variant.SlotId,
                slot => slot.Id,
                (variant, slot) => new
                {
                    slot.ArtifactId,
                    slot.Kind,
                    variant.Id,
                    variant.Url,
                    variant.BlobPath,
                    variant.CreatedAt,
                })
            .OrderBy(item => item.ArtifactId)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(MaxImageCount + 1)
            .ToListAsync(ct);
        var sources = placed.Select(slot => new ExportImageSource(
                slot.ArtifactId,
                slot.Kind,
                slot.PublishedUrl!,
                PublicBlobPath(slot.PublishedUrl!) ?? slot.BaseImagePath))
            .Concat(variants.Select(variant => new ExportImageSource(
                variant.ArtifactId,
                $"{variant.Kind}-take-{variant.Id:N}",
                variant.Url,
                variant.BlobPath)))
            .DistinctBy(source => source.BlobPath ?? source.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source.ArtifactId)
            .ThenBy(source => source.Kind, StringComparer.Ordinal)
            .ThenBy(source => source.SourceUrl, StringComparer.Ordinal)
            .Take(MaxImageCount + 1)
            .ToList();
        var images = await CollectImagesAsync(sources, publicStore, ct);

        return Results.File(
            export.Zip(campaign, artifacts, images),
            "application/zip",
            $"{ExportService.Slug(campaign.Name)}.zip");
    }

    private static async Task<IReadOnlyList<ExportImage>> CollectImagesAsync(
        List<ExportImageSource> sources,
        IPublicContentStore publicStore,
        CancellationToken ct)
    {
        var images = new List<ExportImage>(Math.Min(sources.Count, MaxImageCount));
        var totalBytes = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        foreach (var source in sources.Take(MaxImageCount))
        {
            if (!publicStore.IsConfigured)
            {
                images.Add(Unavailable(source, "storage-not-configured"));
                continue;
            }

            if (source.BlobPath is null)
            {
                images.Add(Unavailable(source, "storage-path-unavailable"));
                continue;
            }

            BoundedContentRead read;
            try
            {
                read = await publicStore.ReadUpToAsync(
                    source.BlobPath, MaxImageBytes, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                images.Add(Unavailable(source, "export-timeout"));
                continue;
            }
            catch (Exception ex) when (ex is Azure.RequestFailedException or HttpRequestException or IOException)
            {
                images.Add(Unavailable(source, "blob-read-failed"));
                continue;
            }

            if (read.ExceedsLimit)
            {
                images.Add(Unavailable(source, "image-size-limit"));
            }
            else if (read.Bytes is not { } bytes)
            {
                images.Add(Unavailable(source, "blob-unavailable"));
            }
            else if (totalBytes + bytes.Length > MaxTotalImageBytes)
            {
                images.Add(Unavailable(source, "archive-size-limit"));
            }
            else
            {
                totalBytes += bytes.Length;
                images.Add(new ExportImage(
                    source.ArtifactId,
                    source.Kind,
                    source.SourceUrl,
                    ContentType(source.SourceUrl),
                    bytes));
            }
        }

        if (sources.Count > MaxImageCount)
        {
            images.Add(new ExportImage(
                null,
                "additional-images",
                string.Empty,
                "application/octet-stream",
                null,
                "image-count-limit"));
        }

        return images;
    }

    private static ExportImage Unavailable(ExportImageSource source, string reason) =>
        new(source.ArtifactId, source.Kind, source.SourceUrl, ContentType(source.SourceUrl), null, reason);

    private static string? PublicBlobPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        const string marker = "/campaigns/";
        var start = uri.AbsolutePath.IndexOf(marker, StringComparison.Ordinal);
        return start < 0 ? null : Uri.UnescapeDataString(uri.AbsolutePath[(start + 1)..]);
    }

    private static string ContentType(string url) => Path.GetExtension(
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => "image/webp",
    };

    private sealed record ExportImageSource(
        Guid? ArtifactId,
        string Kind,
        string SourceUrl,
        string? BlobPath);
}
