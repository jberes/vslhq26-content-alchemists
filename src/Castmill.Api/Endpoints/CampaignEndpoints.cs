using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public static class CampaignEndpoints
{
    public static IEndpointRouteBuilder MapCampaignEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns").RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync).Validate<CampaignCreateRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}", UpdateAsync).Validate<CampaignUpdateRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static CampaignResponse ToResponse(Campaign c) =>
        new(c.Id, c.OwnerId, c.Name, c.Brief, c.CreatedAt, c.UpdatedAt);

    private static async Task<IResult> ListAsync(CastmillDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Campaigns
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => ToResponse(c))
            .ToListAsync(ct));

    private static async Task<IResult> GetAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        // The tenant query filter makes another tenant's campaign a plain 404 —
        // indistinguishable from "does not exist", so nothing leaks.
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        return campaign is null ? Results.NotFound() : Results.Ok(ToResponse(campaign));
    }

    private static async Task<IResult> CreateAsync(
        CampaignCreateRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            OwnerId = AuthEndpoints.GetUserId(principal),
            Name = request.Name,
            Brief = request.Brief,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/campaigns/{campaign.Id}", ToResponse(campaign));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        CampaignUpdateRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        campaign.Name = request.Name;
        campaign.Brief = request.Brief;
        campaign.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(campaign));
    }

    private static async Task<IResult> DeleteAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        // Artifacts have no FK cascade (typed-JSON rows, ADR-003) — delete explicitly.
        await db.Artifacts.Where(a => a.CampaignId == id).ExecuteDeleteAsync(ct);
        db.Campaigns.Remove(campaign);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
