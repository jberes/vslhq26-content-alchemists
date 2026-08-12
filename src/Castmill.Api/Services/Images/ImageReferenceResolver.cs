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

                if (Normalize(item.Asset.Id, memory.ToArray(), item.Link.Kind) is { } reference)
                {
                    result.Add(reference);
                }
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

    /// <summary>
    /// Longest edge of a reference image as sent to a model. Generated output is at most about
    /// one megapixel, so a 4000-pixel source carries no extra information the model can use.
    /// </summary>
    internal const int MaxReferenceEdge = 1536;

    /// <summary>
    /// Hard ceiling per attachment. MAI's edits endpoint refuses anything over 20 MB, and the
    /// gpt-image endpoint accepts 50 MB per image — which is how a card with eight references
    /// quietly built a request of a quarter of a gigabyte.
    /// </summary>
    internal const int MaxReferenceBytes = 12 * 1024 * 1024;

    /// <summary>
    /// Decodes a brand asset and re-encodes it as something a model will actually accept:
    /// downscaled to <see cref="MaxReferenceEdge"/>, and JPEG unless the image has transparency
    /// (a cut-out logo must keep its alpha, so those stay PNG).
    ///
    /// This used to be "PNG at quality 100, original resolution", which turned a phone-sized
    /// photograph into a ~35 MB attachment — over MAI's limit outright, and eight of those on
    /// one card is a multi-hundred-megabyte upload before the model even starts. Returns null
    /// when the bytes are not a decodable image.
    /// </summary>
    internal static ImageReference? Normalize(Guid assetId, byte[] bytes, string kind)
    {
        using var decoded = TryDecode(bytes);
        if (decoded is null)
        {
            return null;
        }

        var opaque = !HasTransparency(decoded);
        byte[]? smallest = null;

        // PNG is lossless, so quality cannot shrink a transparent asset — only resolution can.
        // Hence the outer loop over edge limits, with a JPEG quality step inside it for the
        // opaque case where quality is the cheaper lever.
        foreach (var edge in (int[])[MaxReferenceEdge, 1024, 768])
        {
            using var sized = Downscale(decoded, edge);
            using var image = SKImage.FromBitmap(sized ?? decoded);

            foreach (var quality in opaque ? (int[])[90, 70] : (int[])[100])
            {
                using var encoded = image.Encode(
                    opaque ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, quality);
                if (encoded is null)
                {
                    continue;
                }
                var data = encoded.ToArray();
                smallest = smallest is null || data.Length < smallest.Length ? data : smallest;
                if (data.Length <= MaxReferenceBytes)
                {
                    return Reference(assetId, data, kind, opaque);
                }
            }
        }

        // Nothing fit. Send the smallest we managed anyway: the provider's size refusal is now a
        // legible sentence, which beats silently dropping the reference the user chose.
        return smallest is null ? null : Reference(assetId, smallest, kind, opaque);
    }

    private static ImageReference Reference(Guid assetId, byte[] data, string kind, bool opaque) =>
        new(assetId,
            opaque ? $"{assetId:N}.jpg" : $"{assetId:N}.png",
            opaque ? "image/jpeg" : "image/png",
            data,
            kind);

    /// <summary>
    /// SkiaSharp does not return null for bytes it cannot read — it THROWS
    /// <see cref="ArgumentNullException"/> from inside <c>Decode</c> when no codec matches. So
    /// every `Decode(...) ?? throw`/null-check in this codebase was unreachable; this is the
    /// null-returning decode those call sites assumed they had.
    /// </summary>
    internal static SKBitmap? TryDecode(byte[] bytes)
    {
        try
        {
            return SKBitmap.Decode(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the image actually has see-through pixels — not merely an alpha channel. PNG
    /// screenshots routinely carry a fully opaque one, and treating those as transparent is
    /// what kept a product screenshot on the lossless path and made it enormous. Sampled on a
    /// stride plus the four corners, which is where a cut-out logo's transparency lives.
    /// </summary>
    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.Info.IsOpaque)
        {
            return false;
        }

        const byte threshold = 250;
        var (right, bottom) = (bitmap.Width - 1, bitmap.Height - 1);
        foreach (var (x, y) in ((int X, int Y)[])[(0, 0), (right, 0), (0, bottom), (right, bottom)])
        {
            if (bitmap.GetPixel(x, y).Alpha < threshold)
            {
                return true;
            }
        }

        const int stride = 8;
        for (var y = 0; y < bitmap.Height; y += stride)
        {
            for (var x = 0; x < bitmap.Width; x += stride)
            {
                if (bitmap.GetPixel(x, y).Alpha < threshold)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static SKBitmap? Downscale(SKBitmap source, int maxEdge)
    {
        var scale = Math.Min(1f, (float)maxEdge / Math.Max(source.Width, source.Height));
        return scale >= 1f
            ? null
            : source.Resize(
                new SKImageInfo(
                    Math.Max(1, (int)MathF.Round(source.Width * scale)),
                    Math.Max(1, (int)MathF.Round(source.Height * scale))),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
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
