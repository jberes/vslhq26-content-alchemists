using Azure.Storage.Sas;
using Castmill.Api.Data;
using Castmill.Api.Services.Blob;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

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
}
