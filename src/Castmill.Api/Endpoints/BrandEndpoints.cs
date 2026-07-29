using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public static class BrandEndpoints
{
    public static IEndpointRouteBuilder MapBrandEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/brands").RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync).Validate<BrandProfileRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}", UpdateAsync).Validate<BrandProfileRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static BrandProfileResponse ToResponse(BrandProfile b) =>
        new(b.Id, b.Name, b.StyleCardJson, b.UpdatedAt);

    private static async Task<IResult> ListAsync(CastmillDbContext db, CancellationToken ct) =>
        Results.Ok(await db.BrandProfiles
            .OrderBy(b => b.Name)
            .Select(b => ToResponse(b))
            .ToListAsync(ct));

    private static async Task<IResult> GetAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var brand = await db.BrandProfiles.SingleOrDefaultAsync(b => b.Id == id, ct);
        return brand is null ? Results.NotFound() : Results.Ok(ToResponse(brand));
    }

    private static async Task<IResult> CreateAsync(
        BrandProfileRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var brand = new BrandProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            Name = request.Name,
            StyleCardJson = request.StyleCardJson,
            UpdatedAt = clock.GetUtcNow(),
        };
        db.BrandProfiles.Add(brand);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/brands/{brand.Id}", ToResponse(brand));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        BrandProfileRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var brand = await db.BrandProfiles.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (brand is null)
        {
            return Results.NotFound();
        }

        brand.Name = request.Name;
        brand.StyleCardJson = request.StyleCardJson;
        brand.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(brand));
    }

    private static async Task<IResult> DeleteAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var brand = await db.BrandProfiles.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (brand is null)
        {
            return Results.NotFound();
        }

        db.BrandProfiles.Remove(brand);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
