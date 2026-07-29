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
