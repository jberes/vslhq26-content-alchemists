using Azure.Storage.Sas;
using Castmill.Api.Data;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Images;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

/// <summary>Assets whose preview derivatives the client wants, in one call.</summary>
public sealed record AssetThumbsRequest(IReadOnlyList<Guid>? AssetIds);

/// <summary>
/// A preview URL for one asset. <paramref name="Thumb"/> is false when the derivative could
/// not be produced and the URL points at the full-size original instead — so a client can
/// tell "small and fast" from "correct but heavy" rather than guessing.
/// </summary>
public sealed record AssetThumb(Guid AssetId, string ReadUrl, bool Thumb);

/// <summary>
/// SAS minting (G2): clients never see storage credentials — only short-lived,
/// single-blob, single-operation SAS URLs scoped to assets they own.
/// </summary>
public static class BlobEndpoints
{
    public static IEndpointRouteBuilder MapBlobEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/blob").RequireAuthorization("TenantAllowed");

        group.MapGet("/test", ProbeAsync);
        group.MapPost("/assets/{assetId:guid}/upload-sas", MintUploadAsync).RequireRateLimiting("writes");
        group.MapGet("/assets/{assetId:guid}/read-sas", MintReadAsync);

        // Contact-sheet previews, in ONE request. The kit page used to mint a read SAS per
        // asset, sequentially, and then point <img> at the ORIGINAL — so a 9-face kit paid
        // nine round trips before the first tile could appear and then downloaded tens of
        // megabytes of full-resolution photography to draw thumbnails 140 px wide.
        group.MapPost("/assets/thumbs", MintThumbsAsync).RequireRateLimiting("writes");

        // Proxy upload. The SAS path has the BROWSER PUT straight to blob storage, which makes
        // every upload a cross-origin request and therefore hostage to the storage account's
        // CORS rules — rules that differ per shell (the web client's origin is not the MAUI
        // WebView's) and that nothing in this repo controls. Routing the bytes through the API
        // removes that dependency entirely: this is the same origin every other API call
        // already uses, so if the app can talk to the API at all, it can upload.
        //
        // Kit images are small and infrequent, so the cost of holding them in a request is
        // negligible. The SAS endpoints stay for large media, where it is not.
        group.MapPost("/assets/{assetId:guid}/content", UploadContentAsync)
            .RequireRateLimiting("writes")
            .DisableAntiforgery();
        return routes;
    }

    private static IResult NotConfigured() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "Storage is not configured. Set Storage:AccountName (Entra auth) or Storage:ConnectionString.");

    private static async Task<IResult> ProbeAsync(IBlobSasService sas, CancellationToken ct)
    {
        if (!sas.IsConfigured)
        {
            return NotConfigured();
        }
        var ok = await sas.ProbeAsync(ct);
        return Results.Ok(new { ok });
    }

    private static async Task<IResult> MintUploadAsync(
        Guid assetId, int? minutes, IBlobSasService sas, CastmillDbContext db, CancellationToken ct)
    {
        if (!sas.IsConfigured)
        {
            return NotConfigured();
        }
        // Tenant query filter: another tenant's asset is a plain 404.
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        var url = await sas.MintAsync(asset.BlobPath, BlobSasPermissions.Create | BlobSasPermissions.Write, minutes, ct);
        return Results.Ok(new { uploadUrl = url, blobPath = asset.BlobPath });
    }

    /// <summary>Streams the request body into the asset's private blob.</summary>
    private static async Task<IResult> UploadContentAsync(
        Guid assetId,
        HttpRequest request,
        IBlobSasService sas,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!sas.IsConfigured)
        {
            return NotConfigured();
        }

        // Tenant query filter: another tenant's asset is a plain 404, same as the SAS path.
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        // The declared size is what the asset row was created with; refusing a body that
        // exceeds it stops an upload from quietly becoming something else.
        if (request.ContentLength is { } length && length > asset.SizeBytes)
        {
            return Results.Problem(
                $"Body is larger than the {asset.SizeBytes} bytes this asset was registered with.",
                statusCode: 400);
        }

        await sas.WriteAsync(asset.BlobPath, request.Body, asset.ContentType, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MintReadAsync(
        Guid assetId, int? minutes, IBlobSasService sas, CastmillDbContext db, CancellationToken ct)
    {
        if (!sas.IsConfigured)
        {
            return NotConfigured();
        }
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        var url = await sas.MintAsync(asset.BlobPath, BlobSasPermissions.Read, minutes, ct);
        return Results.Ok(new { readUrl = url });
    }

    /// <summary>Where an asset's cached preview derivative lives. Derived from the id, so no
    /// schema column is needed and existing assets backfill the first time they are shown.</summary>
    internal static string ThumbPath(Guid assetId) => $"derived/thumbs/{assetId:N}.webp";

    /// <summary>Longest edge of a preview derivative — comfortably above the ~140 px the kit
    /// draws, so the same blob also serves a retina grid and the picker's larger tiles.</summary>
    internal const int ThumbMaxEdge = 480;

    private static async Task<IResult> MintThumbsAsync(
        AssetThumbsRequest request,
        int? minutes,
        IBlobSasService sas,
        IImageComposer composer,
        CastmillDbContext db,
        ILogger<AssetThumbsRequest> logger,
        CancellationToken ct)
    {
        if (!sas.IsConfigured)
        {
            return NotConfigured();
        }
        if (request.AssetIds is not { Count: > 0 } requested)
        {
            return Results.Ok(Array.Empty<AssetThumb>());
        }

        // One query, tenant-filtered: ids belonging to another tenant simply do not come back.
        var ids = requested.Distinct().Take(200).ToList();
        var assets = await db.Assets
            .Where(a => ids.Contains(a.Id) && a.ContentType.StartsWith("image/"))
            .Select(a => new { a.Id, a.BlobPath })
            .ToListAsync(ct);

        var results = new List<AssetThumb>(assets.Count);
        foreach (var asset in assets)
        {
            var thumbPath = ThumbPath(asset.Id);
            try
            {
                if (!await sas.ExistsAsync(thumbPath, ct))
                {
                    var opened = await sas.OpenReadAsync(asset.BlobPath, ct);
                    if (opened is null)
                    {
                        continue; // the original is gone — nothing to preview
                    }
                    byte[] original;
                    await using (var source = opened.Value.Stream)
                    {
                        using var buffer = new MemoryStream();
                        await source.CopyToAsync(buffer, ct);
                        original = buffer.ToArray();
                    }
                    var thumb = composer.ToThumbWebp(original, ThumbMaxEdge);
                    using var upload = new MemoryStream(thumb, writable: false);
                    await sas.WriteAsync(thumbPath, upload, "image/webp", ct);
                }

                results.Add(new AssetThumb(
                    asset.Id,
                    (await sas.MintAsync(thumbPath, BlobSasPermissions.Read, minutes, ct)).ToString(),
                    Thumb: true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A preview that cannot be derived (an "image/*" file Skia will not decode,
                // say) must never blank the card: fall back to the original, and log why the
                // cheap path was skipped.
                logger.LogWarning(ex, "Could not derive a preview for asset {AssetId}", asset.Id);
                results.Add(new AssetThumb(
                    asset.Id,
                    (await sas.MintAsync(asset.BlobPath, BlobSasPermissions.Read, minutes, ct)).ToString(),
                    Thumb: false));
            }
        }

        return Results.Ok(results);
    }
}
