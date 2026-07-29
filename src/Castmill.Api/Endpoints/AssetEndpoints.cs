using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

/// <summary>
/// Asset metadata only in B4 — upload/download SAS URLs arrive with the
/// storage work in Phase B3. BlobPath is server-derived, never client-supplied.
/// </summary>
public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/assets").RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).Validate<AssetCreateRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static AssetResponse ToResponse(Asset a) =>
        new(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.BlobPath, a.CreatedAt);

    private static async Task<IResult> ListAsync(CastmillDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Assets
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => ToResponse(a))
            .ToListAsync(ct));

    private static async Task<IResult> CreateAsync(
        AssetCreateRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var tenantId = tenant.TenantId!.Value;
        // Sanitized server-side path: the id segment guarantees uniqueness and
        // the file name is reduced to a safe subset (no traversal, no separators,
        // no dot runs, no leading dot).
        var filtered = string.Concat(request.FileName.Where(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_'));
        while (filtered.Contains("..", StringComparison.Ordinal))
        {
            filtered = filtered.Replace("..", ".", StringComparison.Ordinal);
        }
        var safeName = filtered.TrimStart('.') is { Length: > 0 } s ? s : "file";

        var asset = new Asset
        {
            Id = id,
            TenantId = tenantId,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = request.SizeBytes,
            BlobPath = $"tenants/{tenantId}/assets/{id}/{safeName}",
            CreatedAt = clock.GetUtcNow(),
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/assets/{asset.Id}", ToResponse(asset));
    }

    private static async Task<IResult> DeleteAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        db.Assets.Remove(asset);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
