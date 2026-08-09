using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

/// <summary>
/// Brands: the style card (voice, palette, image style — typed JSON per ADR-003), the
/// asset kit (logos, backgrounds, faces as links onto ordinary Assets) and the content
/// templates (per-generator steering). Everything generation-side reads brands through
/// <see cref="Services.Ai.BrandContextService"/>.
/// </summary>
public static partial class BrandEndpoints
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The kinds a brand asset can be — small on purpose; "other" is the escape hatch.</summary>
    private static readonly string[] AssetKinds = ["logo", "background", "face", "product", "accent", "other"];

    public static IEndpointRouteBuilder MapBrandEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/brands").RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync).Validate<BrandProfileUpsertRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}", UpdateAsync).Validate<BrandProfileUpsertRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");

        // "ai" limiter, not "writes": this spends a model call and fetches a third-party URL.
        group.MapPost("/lookup", LookupAsync).Validate<BrandLookupRequest>().RequireRateLimiting("ai");

        group.MapGet("/{id:guid}/assets", ListAssetsAsync);
        group.MapPost("/{id:guid}/assets", LinkAssetAsync)
            .Validate<BrandAssetLinkRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}/assets/{brandAssetId:guid}", UnlinkAssetAsync).RequireRateLimiting("writes");
        group.MapPatch("/{id:guid}/assets/{brandAssetId:guid}", RenameAssetAsync)
            .Validate<BrandAssetLabelRequest>().RequireRateLimiting("writes");

        group.MapGet("/{id:guid}/templates", ListTemplatesAsync);
        group.MapPost("/{id:guid}/templates", CreateTemplateAsync)
            .Validate<BrandTemplateRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}/templates/{templateId:guid}", UpdateTemplateAsync)
            .Validate<BrandTemplateRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}/templates/{templateId:guid}", DeleteTemplateAsync).RequireRateLimiting("writes");

        return routes;
    }

    /// <summary>Write-side validation only: legacy JSON that predates the schema still
    /// reads back (as RawStyleCardJson with StyleCard = null), never a 500.</summary>
    internal static BrandStyleCard? ParseStyleCard(string? styleCardJson)
    {
        if (string.IsNullOrWhiteSpace(styleCardJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BrandStyleCard>(styleCardJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BrandProfileDetailResponse ToResponse(BrandProfile b) =>
        new(b.Id, b.Name, ParseStyleCard(b.StyleCardJson), b.StyleCardJson, b.UpdatedAt);

    private static IResult? ValidateStyleCard(BrandStyleCard? card)
    {
        if (card is null)
        {
            return null;
        }

        if (card.Colors is { Count: > 12 })
        {
            return Results.Problem("A style card holds at most 12 colours.", statusCode: 400);
        }

        // BrandColor.Hex carries a [RegularExpression], but the Validate<T> filter runs
        // Validator.TryValidateObject, which does NOT recurse into nested objects or
        // collections — so that annotation never ran and any string reached the database.
        // It has to be checked here: these values are composited into images and emitted as
        // CSS, where a non-hex string is a broken render, not a cosmetic problem.
        if (card.Colors?.FirstOrDefault(c => !HexColor().IsMatch(c.Hex ?? string.Empty)) is { } bad)
        {
            return Results.Problem(
                $"Colour '{bad.Role}' must be a six-digit hex value like #0A66C2.", statusCode: 400);
        }

        if (card.BannedPhrases is { } phrases && (phrases.Count > 50 || phrases.Any(p => p.Length > 200)))
        {
            return Results.Problem("At most 50 banned phrases of up to 200 characters each.", statusCode: 400);
        }

        return null;
    }

    /// <summary>
    /// Drafts a style card from a public URL. Returns a DRAFT — nothing is persisted, because
    /// a brand is the thing that steers every generator and must be a human's decision.
    /// </summary>
    private static async Task<IResult> LookupAsync(
        BrandLookupRequest request,
        ClaimsPrincipal principal,
        IBrandLookup lookup,
        CancellationToken ct)
    {
        try
        {
            var result = await lookup.LookupAsync(
                AuthEndpoints.GetUserId(principal), request.Url, request.Notes, ct);
            return Results.Ok(new BrandLookupResponse(
                result.Name, result.StyleCard, result.SourceUrl, result.Notes));
        }
        catch (BrandLookupException ex)
        {
            // A blocked host or an unreachable site is the caller's problem to fix, not a 500.
            return Results.Problem(ex.Message, statusCode: 400);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Results.Problem("Couldn't reach that site.", statusCode: 400);
        }
    }

    private static async Task<IResult> ListAsync(CastmillDbContext db, CancellationToken ct)
    {
        var brands = await db.BrandProfiles.OrderBy(b => b.Name).ToListAsync(ct);
        return Results.Ok(brands.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var brand = await db.BrandProfiles.SingleOrDefaultAsync(b => b.Id == id, ct);
        return brand is null ? Results.NotFound() : Results.Ok(ToResponse(brand));
    }

    private static async Task<IResult> CreateAsync(
        BrandProfileUpsertRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (ValidateStyleCard(request.StyleCard) is { } invalid)
        {
            return invalid;
        }

        var brand = new BrandProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            Name = request.Name,
            StyleCardJson = request.StyleCard is null ? null : JsonSerializer.Serialize(request.StyleCard, Json),
            UpdatedAt = clock.GetUtcNow(),
        };
        db.BrandProfiles.Add(brand);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/brands/{brand.Id}", ToResponse(brand));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        BrandProfileUpsertRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var brand = await db.BrandProfiles.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (brand is null)
        {
            return Results.NotFound();
        }

        if (ValidateStyleCard(request.StyleCard) is { } invalid)
        {
            return invalid;
        }

        brand.Name = request.Name;
        brand.StyleCardJson = request.StyleCard is null ? null : JsonSerializer.Serialize(request.StyleCard, Json);
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

        // Campaigns keep working brandless; the kit rows are meaningless without the brand.
        await db.Campaigns.Where(c => c.BrandId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.BrandId, (Guid?)null), ct);
        await db.BrandAssets.Where(a => a.BrandId == id).ExecuteDeleteAsync(ct);
        await db.BrandTemplates.Where(t => t.BrandId == id).ExecuteDeleteAsync(ct);

        db.BrandProfiles.Remove(brand);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Asset kit --------------------------------------------------------------

    private static async Task<IResult> ListAssetsAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        if (!await db.BrandProfiles.AnyAsync(b => b.Id == id, ct))
        {
            return Results.NotFound();
        }

        var rows = await db.BrandAssets
            .Where(a => a.BrandId == id)
            .Join(db.Assets, ba => ba.AssetId, a => a.Id, (ba, a) => new { ba, a })
            .OrderBy(x => x.ba.Kind).ThenBy(x => x.ba.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(x => new BrandAssetResponse(
            x.ba.Id, x.ba.BrandId, x.ba.AssetId, x.ba.Kind, x.ba.Label,
            x.a.FileName, x.a.ContentType, x.ba.CreatedAt)).ToList());
    }

    private static async Task<IResult> LinkAssetAsync(
        Guid id,
        BrandAssetLinkRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.BrandProfiles.AnyAsync(b => b.Id == id, ct))
        {
            return Results.NotFound();
        }

        if (!AssetKinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Results.Problem($"Kind must be one of: {string.Join(", ", AssetKinds)}.", statusCode: 400);
        }

        // The tenant filter makes a foreign asset a plain "not found" — nothing leaks.
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == request.AssetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        if (await db.BrandAssets.AnyAsync(a => a.BrandId == id && a.AssetId == request.AssetId, ct))
        {
            return Results.Conflict();
        }

        var link = new BrandAsset
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            BrandId = id,
            AssetId = request.AssetId,
            Kind = request.Kind,
            Label = request.Label,
            CreatedAt = clock.GetUtcNow(),
        };
        db.BrandAssets.Add(link);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/brands/{id}/assets/{link.Id}", new BrandAssetResponse(
            link.Id, link.BrandId, link.AssetId, link.Kind, link.Label,
            asset.FileName, asset.ContentType, link.CreatedAt));
    }

    /// <summary>
    /// The label doubles as prompt text ("the host, short dark hair"), so renaming an asset is
    /// a real content decision — it changes what every future image prompt says.
    /// </summary>
    private static async Task<IResult> RenameAssetAsync(
        Guid id, Guid brandAssetId, BrandAssetLabelRequest request,
        CastmillDbContext db, CancellationToken ct)
    {
        var link = await db.BrandAssets.SingleOrDefaultAsync(
            a => a.Id == brandAssetId && a.BrandId == id, ct);
        if (link is null)
        {
            return Results.NotFound();
        }

        link.Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlinkAssetAsync(
        Guid id, Guid brandAssetId, CastmillDbContext db, CancellationToken ct)
    {
        var link = await db.BrandAssets
            .SingleOrDefaultAsync(a => a.Id == brandAssetId && a.BrandId == id, ct);
        if (link is null)
        {
            return Results.NotFound();
        }

        // The link only; the Asset row and its blob remain in the library.
        db.BrandAssets.Remove(link);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Content templates ------------------------------------------------------

    private static BrandTemplateResponse ToResponse(BrandTemplate t) =>
        new(t.Id, t.BrandId, t.Kind, t.Name, t.SteeringPrompt, t.IsDefault, t.UpdatedAt);

    private static bool IsKnownGeneratorKind(string kind) =>
        Generators.Find(kind) is not null || Generators.Normalize(kind) == "blog";

    private static async Task<IResult> ListTemplatesAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        if (!await db.BrandProfiles.AnyAsync(b => b.Id == id, ct))
        {
            return Results.NotFound();
        }

        var templates = await db.BrandTemplates
            .Where(t => t.BrandId == id)
            .OrderBy(t => t.Kind).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return Results.Ok(templates.Select(ToResponse).ToList());
    }

    private static async Task<IResult> CreateTemplateAsync(
        Guid id,
        BrandTemplateRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.BrandProfiles.AnyAsync(b => b.Id == id, ct))
        {
            return Results.NotFound();
        }

        var kind = Generators.Normalize(request.Kind);
        if (!IsKnownGeneratorKind(kind))
        {
            return Results.Problem($"'{request.Kind}' is not a generator kind.", statusCode: 400);
        }

        var now = clock.GetUtcNow();
        if (request.IsDefault)
        {
            // At most one default per (brand, kind) — the new default displaces the old.
            await db.BrandTemplates
                .Where(t => t.BrandId == id && t.Kind == kind && t.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);
        }

        var template = new BrandTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            BrandId = id,
            Kind = kind,
            Name = request.Name,
            SteeringPrompt = request.SteeringPrompt,
            IsDefault = request.IsDefault,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.BrandTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/brands/{id}/templates/{template.Id}", ToResponse(template));
    }

    private static async Task<IResult> UpdateTemplateAsync(
        Guid id,
        Guid templateId,
        BrandTemplateRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var template = await db.BrandTemplates
            .SingleOrDefaultAsync(t => t.Id == templateId && t.BrandId == id, ct);
        if (template is null)
        {
            return Results.NotFound();
        }

        var kind = Generators.Normalize(request.Kind);
        if (!IsKnownGeneratorKind(kind))
        {
            return Results.Problem($"'{request.Kind}' is not a generator kind.", statusCode: 400);
        }

        if (request.IsDefault && (!template.IsDefault || template.Kind != kind))
        {
            await db.BrandTemplates
                .Where(t => t.BrandId == id && t.Kind == kind && t.IsDefault && t.Id != templateId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);
        }

        template.Kind = kind;
        template.Name = request.Name;
        template.SteeringPrompt = request.SteeringPrompt;
        template.IsDefault = request.IsDefault;
        template.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(template));
    }

    private static async Task<IResult> DeleteTemplateAsync(
        Guid id, Guid templateId, CastmillDbContext db, CancellationToken ct)
    {
        var template = await db.BrandTemplates
            .SingleOrDefaultAsync(t => t.Id == templateId && t.BrandId == id, ct);
        if (template is null)
        {
            return Results.NotFound();
        }

        db.BrandTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
