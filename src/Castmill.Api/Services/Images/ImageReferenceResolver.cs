using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Blob;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Castmill.Api.Services.Images;

/// <summary>One real input image sent to an image-capable model.</summary>
public sealed record ImageReference(Guid AssetId, string FileName, string ContentType, byte[] Bytes, string Kind);

public interface IImageReferenceResolver
{
    /// <summary>Explicit per-card references plus up to three product screenshots from the
    /// campaign brand. Product screenshots always attach (the product-fidelity rule).</summary>
    Task<IReadOnlyList<ImageReference>> ResolveAsync(
        Campaign campaign, ImageSlot slot, CancellationToken ct);
}

public sealed class ImageReferenceResolver(
    CastmillDbContext db,
    IBlobSasService blobs,
    ILogger<ImageReferenceResolver> logger) : IImageReferenceResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ImageReference>> ResolveAsync(
        Campaign campaign, ImageSlot slot, CancellationToken ct)
    {
        if (campaign.BrandId is not { } brandId || !blobs.IsConfigured)
        {
            return [];
        }

        var selected = ParseIds(slot.ReferenceAssetIdsJson);
        var links = await db.BrandAssets
            .Where(a => a.BrandId == brandId
                && (selected.Contains(a.Id) || a.Kind == "product"))
            .Join(db.Assets, link => link.AssetId, asset => asset.Id,
                (link, asset) => new { Link = link, Asset = asset })
            .OrderByDescending(x => x.Link.Kind == "product")
            .ThenBy(x => x.Link.CreatedAt)
            .ToListAsync(ct);

        // Product assets are automatic but bounded; explicit references remain under the
        // user's control. The final cap protects both provider limits and request size.
        var wanted = links
            .Where(x => x.Link.Kind != "product").Take(5)
            .Concat(links.Where(x => x.Link.Kind == "product").Take(3))
            .DistinctBy(x => x.Asset.Id)
            .Take(8)
            .ToList();

        var result = new List<ImageReference>(wanted.Count);
        foreach (var item in wanted)
        {
            try
            {
                var opened = await blobs.OpenReadAsync(item.Asset.BlobPath, ct);
                if (opened is null || opened.Value.Length > 50L * 1024 * 1024)
                {
                    continue;
                }

                await using var source = opened.Value.Stream;
                using var memory = new MemoryStream();
                await source.CopyToAsync(memory, ct);

                // Azure's edit endpoint accepts PNG/JPEG. Normalizing here means WebP brand
                // assets work too and providers never need to guess a filename's true type.
                using var bitmap = SKBitmap.Decode(memory.ToArray());
                if (bitmap is null)
                {
                    continue;
                }
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                result.Add(new ImageReference(
                    item.Asset.Id, $"{item.Asset.Id:N}.png", "image/png", encoded.ToArray(), item.Link.Kind));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reference attachment is soft-fail: a missing old asset must not make the
                // content card impossible to render. Product fidelity still uses what remains.
                logger.LogWarning(ex, "Could not load image reference {AssetId}", item.Asset.Id);
            }
        }

        return result;
    }

    internal static IReadOnlySet<Guid> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HashSet<Guid>();
        }
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json, Json)?.ToHashSet() ?? [];
        }
        catch (JsonException)
        {
            return new HashSet<Guid>();
        }
    }
}
