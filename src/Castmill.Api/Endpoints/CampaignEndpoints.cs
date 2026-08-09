using System.Text.Json;
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
        // The whole workspace dashboard in one query set — the front page and the
        // campaigns index used to fetch a full preview per campaign to derive this.
        group.MapGet("/dashboard", DashboardAsync);
        group.MapGet("/{id:guid}", GetAsync);
        // One call feeds the campaign header counter, the front page's "slots
        // waiting" block and Focus Mode's slot list (G9) — no per-surface polling.
        group.MapGet("/{id:guid}/preview", PreviewAsync);
        group.MapPost("/", CreateAsync).Validate<CampaignCreateRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}", UpdateAsync).Validate<CampaignUpdateRequest>().RequireRateLimiting("writes");

        // The chosen SEO/AEO targets. Separate from the campaign PUT because they are written
        // by a different step, by a different decision, and read by every generator.
        group.MapGet("/{id:guid}/seo-targets", GetSeoTargetsAsync);
        group.MapPut("/{id:guid}/seo-targets", SetSeoTargetsAsync)
            .Validate<SeoTargetsRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    internal static CampaignResponse ToResponse(Campaign c) =>
        new(c.Id, c.OwnerId, c.Name, c.Brief, c.CreatedAt, c.UpdatedAt, c.BrandId, ParseLinks(c.ContextJson));

    private static readonly JsonSerializerOptions TargetsJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Stored JSON that predates the schema, or was hand-edited, must read back as "no
    /// targets" rather than 500 — the same forgiving contract ParseLinks already uses.
    /// </summary>
    internal static SeoTargetsResponse ParseSeoTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SeoTargetsResponse(null, [], []);
        }

        try
        {
            return JsonSerializer.Deserialize<SeoTargetsResponse>(json, TargetsJson)
                   ?? new SeoTargetsResponse(null, [], []);
        }
        catch (JsonException)
        {
            return new SeoTargetsResponse(null, [], []);
        }
    }

    private static async Task<IResult> GetSeoTargetsAsync(
        Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        return campaign is null
            ? Results.NotFound()
            : Results.Ok(ParseSeoTargets(campaign.SeoTargetsJson));
    }

    private static async Task<IResult> SetSeoTargetsAsync(
        Guid id,
        SeoTargetsRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        var keywords = request.Keywords ?? [];
        var questions = request.Questions ?? [];

        // The primary must be one of the chosen keywords, or the steering block would name a
        // target the rest of the brief never mentions.
        var primary = string.IsNullOrWhiteSpace(request.PrimaryKeyword)
            ? (keywords.Count > 0 ? keywords[0].Term : null)
            : request.PrimaryKeyword.Trim();

        if (primary is not null
            && !keywords.Any(k => string.Equals(k.Term, primary, StringComparison.OrdinalIgnoreCase)))
        {
            keywords = [new SeoTarget(primary), .. keywords];
        }

        var stored = new SeoTargetsResponse(primary, keywords, questions);
        campaign.SeoTargetsJson = keywords.Count == 0 && questions.Count == 0
            ? null   // clearing is a real action, not an empty object
            : JsonSerializer.Serialize(stored, TargetsJson);
        campaign.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return Results.Ok(stored);
    }

    internal static IReadOnlyList<CampaignLink>? ParseLinks(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<CampaignLink>>(contextJson, Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Validates the brand/links half of a create or update; null means valid.</summary>
    private static async Task<IResult?> ValidateBrandAndLinksAsync(
        Guid? brandId, IReadOnlyList<CampaignLink>? links, CastmillDbContext db, CancellationToken ct)
    {
        if (links is { Count: > 10 })
        {
            return Results.Problem("A campaign holds at most 10 context links.", statusCode: 400);
        }

        // The tenant filter makes a foreign brand indistinguishable from a missing one.
        if (brandId is { } id && !await db.BrandProfiles.AnyAsync(b => b.Id == id, ct))
        {
            return Results.Problem("That brand does not exist.", statusCode: 400);
        }

        return null;
    }

    private static async Task<IResult> ListAsync(CastmillDbContext db, CancellationToken ct)
    {
        var campaigns = await db.Campaigns
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
        return Results.Ok(campaigns.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        // The tenant query filter makes another tenant's campaign a plain 404 —
        // indistinguishable from "does not exist", so nothing leaks.
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        return campaign is null ? Results.NotFound() : Results.Ok(ToResponse(campaign));
    }

    /// <summary>
    /// Campaign + artifact previews + image-slot state in one payload (ADR-003 keeps
    /// the heavy content out). This is what every image counter reads.
    /// </summary>
    private static async Task<IResult> PreviewAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }
        var artifactRows = await db.Artifacts
            .Where(a => a.CampaignId == id)
            .OrderBy(a => a.Kind).ThenBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.CampaignId, a.Kind, a.Title, a.Status, a.Version, a.CreatedAt, a.UpdatedAt, a.CitationsJson })
            .ToListAsync(ct);
        var artifacts = artifactRows
            .Select(a => new ArtifactPreviewResponse(
                a.Id, a.CampaignId, a.Kind, a.Title, a.Status, a.Version, a.CreatedAt, a.UpdatedAt,
                ArtifactEndpoints.ParseCitations(a.CitationsJson)))
            .ToList();
        var slots = await db.ImageSlots.Where(s => s.CampaignId == id).ToListAsync(ct);

        var brand = campaign.BrandId is { } brandId
            ? await db.BrandProfiles
                .Where(b => b.Id == brandId)
                .Select(b => new BrandSummaryResponse(b.Id, b.Name))
                .SingleOrDefaultAsync(ct)
            : null;

        return Results.Ok(new
        {
            campaign = ToResponse(campaign),
            brand,
            artifacts,
            imageSlots = slots
                .OrderBy(s => Array.FindIndex(Services.Images.ImagePlanService.Templates, t => t.Kind == s.Kind))
                .Select(ImageSlotEndpoints.ToResponse)
                .ToList(),
            imagesFilled = slots.Count(s => s.State == "Filled"),
            imagesTotal = slots.Count,
        });
    }

    /// <summary>
    /// Cross-campaign dashboard projection: the review queue, aging drafts, per-campaign
    /// counters and the empty-slot summary — grouped server-side so the payload stays a
    /// few KB regardless of how much content the campaigns hold.
    /// </summary>
    private static async Task<IResult> DashboardAsync(
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var campaigns = await db.Campaigns
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new { c.Id, c.Name, c.UpdatedAt })
            .ToListAsync(ct);
        var names = campaigns.ToDictionary(c => c.Id, c => c.Name);

        var artifactCounts = await db.Artifacts
            .GroupBy(a => a.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Total = g.Count(),
                InReview = g.Count(a => a.Status == ArtifactStatus.InReview),
            })
            .ToListAsync(ct);

        var slotCounts = await db.ImageSlots
            .GroupBy(s => s.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Total = g.Count(),
                Filled = g.Count(s => s.State == "Filled"),
            })
            .ToListAsync(ct);

        var review = await db.Artifacts
            .Where(a => a.Status == ArtifactStatus.InReview)
            .OrderByDescending(a => a.UpdatedAt)
            .Take(50)
            .Select(a => new { a.CampaignId, a.Id, a.Kind, a.Title, a.Status, a.UpdatedAt })
            .ToListAsync(ct);

        var agingCutoff = clock.GetUtcNow() - TimeSpan.FromDays(7);
        var aging = await db.Artifacts
            .Where(a => a.Status == ArtifactStatus.Draft
                && a.Kind != "transcript"
                && a.UpdatedAt < agingCutoff)
            .OrderBy(a => a.UpdatedAt)
            .Take(50)
            .Select(a => new { a.CampaignId, a.Id, a.Kind, a.Title, a.Status, a.UpdatedAt })
            .ToListAsync(ct);

        // The Wire's queue: reviewed and waiting for a slot (ADR-F22's Queued state).
        var readyToSchedule = await db.Artifacts
            .Where(a => a.Status == ArtifactStatus.Queued)
            .OrderBy(a => a.UpdatedAt)
            .Take(50)
            .Select(a => new { a.CampaignId, a.Id, a.Kind, a.Title, a.Status, a.UpdatedAt })
            .ToListAsync(ct);

        var emptySlotModels = await db.ImageSlots
            .Where(s => s.State != "Filled" && s.ModelAlias != null)
            .Select(s => s.ModelAlias!)
            .Distinct()
            .ToListAsync(ct);

        var counts = campaigns.Select(c =>
        {
            var a = artifactCounts.FirstOrDefault(x => x.CampaignId == c.Id);
            var s = slotCounts.FirstOrDefault(x => x.CampaignId == c.Id);
            return new CampaignCounts(c.Id, a?.Total ?? 0, a?.InReview ?? 0, s?.Filled ?? 0, s?.Total ?? 0);
        }).ToList();

        var withEmpty = slotCounts.Where(s => s.Total > s.Filled).Select(s => s.CampaignId).ToHashSet();

        return Results.Ok(new DashboardResponse(
            review.Select(a => new DashboardArtifact(
                a.CampaignId, names.GetValueOrDefault(a.CampaignId, ""), a.Id,
                a.Kind, a.Title, a.Status, a.UpdatedAt)).ToList(),
            aging.Select(a => new DashboardArtifact(
                a.CampaignId, names.GetValueOrDefault(a.CampaignId, ""), a.Id,
                a.Kind, a.Title, a.Status, a.UpdatedAt)).ToList(),
            counts,
            slotCounts.Sum(s => s.Total - s.Filled),
            withEmpty.Count,
            emptySlotModels,
            campaigns.FirstOrDefault(c => withEmpty.Contains(c.Id))?.Id,
            readyToSchedule.Select(a => new DashboardArtifact(
                a.CampaignId, names.GetValueOrDefault(a.CampaignId, ""), a.Id,
                a.Kind, a.Title, a.Status, a.UpdatedAt)).ToList()));
    }

    private static async Task<IResult> CreateAsync(
        CampaignCreateRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await ValidateBrandAndLinksAsync(request.BrandId, request.Links, db, ct) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            OwnerId = AuthEndpoints.GetUserId(principal),
            Name = request.Name,
            Brief = request.Brief,
            BrandId = request.BrandId,
            ContextJson = request.Links is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(request.Links, Json),
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

        if (await ValidateBrandAndLinksAsync(request.BrandId, request.Links, db, ct) is { } invalid)
        {
            return invalid;
        }

        campaign.Name = request.Name;
        campaign.Brief = request.Brief;
        campaign.BrandId = request.BrandId;
        campaign.ContextJson = request.Links is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(request.Links, Json);
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

        // Children have no FK cascade (typed-JSON rows, ADR-003) — delete explicitly.
        await db.ArtifactRevisions
            .Where(r => db.Artifacts.Any(a => a.Id == r.ArtifactId && a.CampaignId == id))
            .ExecuteDeleteAsync(ct);
        await db.Artifacts.Where(a => a.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.ImageVariants.Where(v => v.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.ImageSlots.Where(s => s.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.ScheduleEntries.Where(s => s.CampaignId == id).ExecuteDeleteAsync(ct);
        await db.GenerationRuns.Where(r => r.CampaignId == id).ExecuteDeleteAsync(ct);
        db.Campaigns.Remove(campaign);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
