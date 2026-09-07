using System.Security.Claims;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Brands;
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
        group.MapPut("/{id:guid}", UpdateAsync).Validate<BrandProfileUpsertRequest>()
            .RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapGet("/{id:guid}/collaborators", ListCollaboratorsAsync);
        group.MapPost("/{id:guid}/collaborators", AddCollaboratorAsync)
            .Validate<BrandCollaboratorRequest>().RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapDelete("/{id:guid}/collaborators/{collaboratorId:guid}", RemoveCollaboratorAsync)
            .RequireRateLimiting("writes").SerializeBrandWrite();

        // "ai" limiter, not "writes": this spends a model call and fetches a third-party URL.
        group.MapPost("/lookup", LookupAsync).Validate<BrandLookupRequest>().RequireRateLimiting("ai");

        group.MapGet("/{id:guid}/assets", ListAssetsAsync);
        group.MapPost("/{id:guid}/assets", LinkAssetAsync)
            .Validate<BrandAssetLinkRequest>().RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapDelete("/{id:guid}/assets/{brandAssetId:guid}", UnlinkAssetAsync)
            .RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapPatch("/{id:guid}/assets/{brandAssetId:guid}", RenameAssetAsync)
            .Validate<BrandAssetLabelRequest>().RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapPatch("/{id:guid}/assets/{brandAssetId:guid}/kind", ChangeAssetKindAsync)
            .Validate<BrandAssetKindRequest>().RequireRateLimiting("writes").SerializeBrandWrite();

        // Brand knowledge (ADR-056): RAG endpoints, skills, MCP servers.
        group.MapGet("/{id:guid}/knowledge", GetKnowledgeAsync);
        group.MapPost("/{id:guid}/knowledge/sources", UpsertKnowledgeSourceAsync)
            .Validate<BrandKnowledgeSourceRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}/knowledge/sources/{itemId:guid}", UpsertKnowledgeSourceAsync)
            .Validate<BrandKnowledgeSourceRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}/knowledge/sources/{itemId:guid}", DeleteKnowledgeSourceAsync).RequireRateLimiting("writes");
        group.MapPost("/{id:guid}/knowledge/sources/copy", CopyKnowledgeSourceAsync)
            .Validate<BrandKnowledgeSourceCopyRequest>().RequireRateLimiting("writes");
        group.MapPost("/{id:guid}/knowledge/skills", UpsertSkillAsync)
            .Validate<BrandSkillRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}/knowledge/skills/{itemId:guid}", UpsertSkillAsync)
            .Validate<BrandSkillRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}/knowledge/skills/{itemId:guid}", DeleteSkillAsync).RequireRateLimiting("writes");
        group.MapPost("/{id:guid}/knowledge/mcp-servers", UpsertMcpServerAsync)
            .Validate<BrandMcpServerRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}/knowledge/mcp-servers/{itemId:guid}", UpsertMcpServerAsync)
            .Validate<BrandMcpServerRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}/knowledge/mcp-servers/{itemId:guid}", DeleteMcpServerAsync).RequireRateLimiting("writes");

        group.MapGet("/{id:guid}/templates", ListTemplatesAsync);
        group.MapPost("/{id:guid}/templates", CreateTemplateAsync)
            .Validate<BrandTemplateRequest>().RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapPut("/{id:guid}/templates/{templateId:guid}", UpdateTemplateAsync)
            .Validate<BrandTemplateRequest>().RequireRateLimiting("writes").SerializeBrandWrite();
        group.MapDelete("/{id:guid}/templates/{templateId:guid}", DeleteTemplateAsync)
            .RequireRateLimiting("writes").SerializeBrandWrite();

        return routes;
    }

    private static RouteHandlerBuilder SerializeBrandWrite(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (context, next) =>
        {
            if (!Guid.TryParse(context.HttpContext.Request.RouteValues["id"]?.ToString(), out var brandId))
            {
                return Results.NotFound();
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<CastmillDbContext>();
            var strategy = new NonReplayingExecutionStrategy(db);
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, context.HttpContext.RequestAborted);
                await AcquireBrandLockAsync(db, brandId, context.HttpContext.RequestAborted);
                var result = await next(context);
                await transaction.CommitAsync(context.HttpContext.RequestAborted);
                return result;
            });
        });

    internal static Task AcquireBrandLockAsync(
        CastmillDbContext db,
        Guid brandId,
        CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {"castmill:brand:" + brandId.ToString("N")},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;
            IF @result < 0
                THROW 51000, 'Could not acquire the Brand mutation lock.', 1;
            """, ct);

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

    private static BrandProfileDetailResponse ToResponse(BrandProfile brand, bool isOwner = true) =>
        new(brand.Id, brand.Name, ParseStyleCard(brand.StyleCardJson), brand.StyleCardJson,
            brand.UpdatedAt, isOwner);

    private static Task<BrandAccess?> FindAccessAsync(
        Guid brandId,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        bool tracking,
        CancellationToken ct) =>
        access.FindAsync(
            brandId,
            AuthEndpoints.GetUserId(principal),
            tenant.TenantId!.Value,
            tracking,
            ct);

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

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CancellationToken ct)
    {
        var brands = await access.ListAsync(
            AuthEndpoints.GetUserId(principal), tenant.TenantId!.Value, ct);
        return Results.Ok(brands.Select(item => ToResponse(item.Brand, item.IsOwner)).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        return grant is null
            ? Results.NotFound()
            : Results.Ok(ToResponse(grant.Brand, grant.IsOwner));
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
        SeedStarterTemplates(db, brand, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/brands/{brand.Id}", ToResponse(brand));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        BrandProfileUpsertRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: true, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }

        if (ValidateStyleCard(request.StyleCard) is { } invalid)
        {
            return invalid;
        }

        grant.Brand.Name = request.Name;
        grant.Brand.StyleCardJson = request.StyleCard is null
            ? null
            : JsonSerializer.Serialize(request.StyleCard, Json);
        grant.Brand.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(grant.Brand, grant.IsOwner));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: true, ct);
        if (grant is null || !grant.IsOwner)
        {
            return Results.NotFound();
        }

        // Campaigns keep working brandless; the kit rows are meaningless without the brand.
        await db.Campaigns.IgnoreQueryFilters().Where(c => c.BrandId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.BrandId, (Guid?)null), ct);
        await db.BrandAssets.IgnoreQueryFilters()
            .Where(item => item.BrandId == id && item.TenantId == grant.Brand.TenantId)
            .ExecuteDeleteAsync(ct);
        await db.BrandTemplates.IgnoreQueryFilters()
            .Where(item => item.BrandId == id && item.TenantId == grant.Brand.TenantId)
            .ExecuteDeleteAsync(ct);

        db.BrandProfiles.Remove(grant.Brand);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListCollaboratorsAsync(
        Guid id,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null || !grant.IsOwner)
        {
            return Results.NotFound();
        }

        var collaborators = await db.BrandCollaborators
            .Where(item => item.BrandId == id)
            .Join(db.Users, item => item.UserId, user => user.Id, (item, user) => new
            {
                item.Id,
                item.UserId,
                item.Email,
                user.DisplayName,
                item.GrantedAt,
            })
            .OrderBy(item => item.Email)
            .Select(item => new BrandCollaboratorResponse(
                item.Id, item.UserId, item.Email, item.DisplayName, item.GrantedAt))
            .ToListAsync(ct);
        return Results.Ok(collaborators);
    }

    private static async Task<IResult> AddCollaboratorAsync(
        Guid id,
        BrandCollaboratorRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var ownerId = AuthEndpoints.GetUserId(principal);
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null || !grant.IsOwner)
        {
            return Results.NotFound();
        }

        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, ct);
        if (user is null || user.Id == ownerId || user.TenantId == grant.Brand.TenantId)
        {
            return Results.Problem(
                "That account is not available for sharing.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (await db.BrandCollaborators.AnyAsync(
                item => item.BrandId == id && item.UserId == user.Id, ct))
        {
            return Results.Conflict(new { detail = "That account already has access." });
        }

        var collaborator = new BrandCollaborator
        {
            Id = Guid.NewGuid(),
            TenantId = grant.Brand.TenantId,
            BrandId = id,
            UserId = user.Id,
            GrantedByUserId = ownerId,
            Email = user.Email ?? request.Email.Trim(),
            GrantedAt = clock.GetUtcNow(),
        };
        db.BrandCollaborators.Add(collaborator);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/brands/{id}/collaborators/{collaborator.Id}",
            new BrandCollaboratorResponse(
                collaborator.Id, user.Id, collaborator.Email, user.DisplayName,
                collaborator.GrantedAt));
    }

    private static async Task<IResult> RemoveCollaboratorAsync(
        Guid id,
        Guid collaboratorId,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null || !grant.IsOwner)
        {
            return Results.NotFound();
        }

        var collaborator = await db.BrandCollaborators.SingleOrDefaultAsync(
            item => item.Id == collaboratorId && item.BrandId == id, ct);
        if (collaborator is null)
        {
            return Results.NotFound();
        }

        var collaboratorTenantId = await db.Users
            .Where(user => user.Id == collaborator.UserId)
            .Select(user => user.TenantId)
            .SingleAsync(ct);
        await db.Campaigns.IgnoreQueryFilters()
            .Where(campaign => campaign.TenantId == collaboratorTenantId
                && campaign.BrandId == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(campaign => campaign.BrandId, (Guid?)null), ct);
        var collaboratorAssetIds = db.Assets.IgnoreQueryFilters()
            .Where(asset => asset.TenantId == collaboratorTenantId)
            .Select(asset => asset.Id);
        await db.BrandAssets.IgnoreQueryFilters()
            .Where(link => link.BrandId == id
                && link.TenantId == grant.Brand.TenantId
                && collaboratorAssetIds.Contains(link.AssetId))
            .ExecuteDeleteAsync(ct);

        db.BrandCollaborators.Remove(collaborator);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Asset kit --------------------------------------------------------------

    private static async Task<IResult> ListAssetsAsync(
        Guid id,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }

        var rows = await db.BrandAssets.IgnoreQueryFilters()
            .Where(item => item.BrandId == id && item.TenantId == grant.Brand.TenantId)
            .Join(db.Assets.IgnoreQueryFilters(), item => item.AssetId, asset => asset.Id,
                (item, asset) => new { ba = item, a = asset })
            .OrderBy(x => x.ba.Kind).ThenBy(x => x.ba.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(x => new BrandAssetResponse(
            x.ba.Id, x.ba.BrandId, x.ba.AssetId, x.ba.Kind, x.ba.Label,
            x.a.FileName, x.a.ContentType, x.ba.CreatedAt)).ToList());
    }

    private static async Task<IResult> LinkAssetAsync(
        Guid id,
        BrandAssetLinkRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService access,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
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

        if (await db.BrandAssets.IgnoreQueryFilters().AnyAsync(
            item => item.BrandId == id && item.AssetId == request.AssetId, ct))
        {
            return Results.Conflict();
        }

        var link = new BrandAsset
        {
            Id = Guid.NewGuid(),
            TenantId = grant.Brand.TenantId,
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
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var link = await db.BrandAssets.IgnoreQueryFilters().SingleOrDefaultAsync(
            item => item.Id == brandAssetId && item.BrandId == id
                && item.TenantId == grant.Brand.TenantId, ct);
        if (link is null)
        {
            return Results.NotFound();
        }

        link.Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>The file stays put; only its role in the Brand kit changes.</summary>
    private static async Task<IResult> ChangeAssetKindAsync(
        Guid id, Guid brandAssetId, BrandAssetKindRequest request,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var kind = request.Kind.Trim().ToLowerInvariant();
        if (!AssetKinds.Contains(kind, StringComparer.Ordinal))
        {
            return Results.Problem(
                $"Kind must be one of: {string.Join(", ", AssetKinds)}.", statusCode: 400);
        }

        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var link = await db.BrandAssets.IgnoreQueryFilters().SingleOrDefaultAsync(
            item => item.Id == brandAssetId && item.BrandId == id
                && item.TenantId == grant.Brand.TenantId, ct);
        if (link is null)
        {
            return Results.NotFound();
        }

        link.Kind = kind;
        await db.SaveChangesAsync(ct);

        var asset = await db.Assets.IgnoreQueryFilters().SingleAsync(item => item.Id == link.AssetId, ct);
        return Results.Ok(new BrandAssetResponse(
            link.Id, link.BrandId, link.AssetId, link.Kind, link.Label,
            asset.FileName, asset.ContentType, link.CreatedAt));
    }

    private static async Task<IResult> UnlinkAssetAsync(
        Guid id, Guid brandAssetId,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var link = await db.BrandAssets.IgnoreQueryFilters().SingleOrDefaultAsync(
            item => item.Id == brandAssetId && item.BrandId == id
                && item.TenantId == grant.Brand.TenantId, ct);
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
        ArtifactKinds.IsUserContent(Generators.Normalize(kind))
        && (Generators.Find(kind) is not null || Generators.Normalize(kind) == "blog");

    private static async Task<IResult> ListTemplatesAsync(
        Guid id,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }

        var templates = await db.BrandTemplates.IgnoreQueryFilters()
            .Where(item => item.BrandId == id && item.TenantId == grant.Brand.TenantId)
            .OrderBy(t => t.Kind).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return Results.Ok(templates.Select(ToResponse).ToList());
    }

    // ---- Brand knowledge (ADR-056) ------------------------------------------------

    private static async Task<IResult> GetKnowledgeAsync(
        Guid id, ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var tenantId = grant.Brand.TenantId;
        var sources = await db.BrandKnowledgeSources.IgnoreQueryFilters()
            .Where(k => k.BrandId == id && k.TenantId == tenantId).OrderBy(k => k.CreatedAt).ToListAsync(ct);
        var skills = await db.BrandSkills.IgnoreQueryFilters()
            .Where(k => k.BrandId == id && k.TenantId == tenantId).OrderBy(k => k.Name).ToListAsync(ct);
        var servers = await db.BrandMcpServers.IgnoreQueryFilters()
            .Where(k => k.BrandId == id && k.TenantId == tenantId).OrderBy(k => k.Name).ToListAsync(ct);
        return Results.Ok(new BrandKnowledgeResponse(
            sources.Select(ToResponse).ToList(),
            skills.Select(ToResponse).ToList(),
            servers.Select(ToResponse).ToList()));
    }

    private static async Task<IResult> UpsertKnowledgeSourceAsync(
        Guid id, Guid? itemId, BrandKnowledgeSourceRequest request,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        Castmill.Api.Services.Secrets.ISecretCipher cipher,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var now = clock.GetUtcNow();
        BrandKnowledgeSource? row = null;
        if (itemId is { } existingId)
        {
            row = await db.BrandKnowledgeSources.IgnoreQueryFilters()
                .SingleOrDefaultAsync(k => k.Id == existingId && k.BrandId == id && k.TenantId == grant.Brand.TenantId, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
        }
        row ??= new BrandKnowledgeSource
        {
            Id = Guid.NewGuid(), TenantId = grant.Brand.TenantId, BrandId = id,
            Name = request.Name, BaseUrl = request.BaseUrl, CreatedAt = now,
        };
        row.Name = request.Name.Trim();
        row.BaseUrl = request.BaseUrl.Trim();
        row.QueryPath = string.IsNullOrWhiteSpace(request.QueryPath) ? "/query" : request.QueryPath.Trim();
        row.QueryField = string.IsNullOrWhiteSpace(request.QueryField) ? "query" : request.QueryField.Trim();
        row.ProductType = string.IsNullOrWhiteSpace(request.ProductType) ? null : request.ProductType.Trim();
        row.ProductField = string.IsNullOrWhiteSpace(request.ProductField) ? "productType" : request.ProductField.Trim();
        row.Enabled = request.Enabled;
        if (request.Token is not null)
        {
            // Null leaves the stored token; empty clears it. Encrypted with the workspace cipher,
            // never stored or returned in clear.
            row.TokenCiphertext = request.Token.Length == 0 ? null : cipher.Encrypt(request.Token);
        }
        row.UpdatedAt = now;
        if (itemId is null)
        {
            db.BrandKnowledgeSources.Add(row);
        }
        await db.SaveChangesAsync(ct);
        return itemId is null
            ? Results.Created($"/api/v1/brands/{id}/knowledge/sources/{row.Id}", ToResponse(row))
            : Results.Ok(ToResponse(row));
    }

    /// <summary>
    /// Copies another brand's gateway settings onto this brand (ADR-061), token included, with
    /// only the product changed. Server-side because the token is write-only. The caller must
    /// hold access to BOTH brands: without that check, access to one brand would let someone
    /// mint a working copy of a gateway credential they were never granted.
    /// </summary>
    private static async Task<IResult> CopyKnowledgeSourceAsync(
        Guid id, BrandKnowledgeSourceCopyRequest request, ClaimsPrincipal principal,
        ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var target = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        var source = await FindAccessAsync(request.SourceBrandId, principal, tenant, access, tracking: false, ct);
        if (target is null || source is null)
        {
            return Results.NotFound();
        }
        // Same tenant only: the token ciphertext is copied verbatim, and it is readable only
        // under the workspace cipher that encrypted it.
        if (target.Brand.TenantId != source.Brand.TenantId)
        {
            return Results.NotFound();
        }
        var row = await db.BrandKnowledgeSources.IgnoreQueryFilters().SingleOrDefaultAsync(
            k => k.Id == request.SourceId && k.BrandId == request.SourceBrandId
                && k.TenantId == source.Brand.TenantId, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var copy = new BrandKnowledgeSource
        {
            Id = Guid.NewGuid(),
            TenantId = target.Brand.TenantId,
            BrandId = id,
            Name = string.IsNullOrWhiteSpace(request.Name) ? row.Name : request.Name.Trim(),
            BaseUrl = row.BaseUrl,
            QueryPath = row.QueryPath,
            QueryField = row.QueryField,
            ProductType = string.IsNullOrWhiteSpace(request.ProductType) ? row.ProductType : request.ProductType.Trim(),
            ProductField = row.ProductField,
            TokenCiphertext = row.TokenCiphertext,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.BrandKnowledgeSources.Add(copy);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/brands/{id}/knowledge/sources/{copy.Id}", ToResponse(copy));
    }

    private static async Task<IResult> DeleteKnowledgeSourceAsync(
        Guid id, Guid itemId, ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var removed = await db.BrandKnowledgeSources.IgnoreQueryFilters()
            .Where(k => k.Id == itemId && k.BrandId == id && k.TenantId == grant.Brand.TenantId)
            .ExecuteDeleteAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> UpsertSkillAsync(
        Guid id, Guid? itemId, BrandSkillRequest request,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var now = clock.GetUtcNow();
        BrandSkill? row = null;
        if (itemId is { } existingId)
        {
            row = await db.BrandSkills.IgnoreQueryFilters()
                .SingleOrDefaultAsync(k => k.Id == existingId && k.BrandId == id && k.TenantId == grant.Brand.TenantId, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
        }
        row ??= new BrandSkill
        {
            Id = Guid.NewGuid(), TenantId = grant.Brand.TenantId, BrandId = id,
            Name = request.Name, FileName = request.FileName, Content = request.Content, CreatedAt = now,
        };
        row.Name = request.Name.Trim();
        row.FileName = request.FileName.Trim();
        row.Content = request.Content;
        row.AppliesTo = string.IsNullOrWhiteSpace(request.AppliesTo) ? null : request.AppliesTo.Trim();
        row.Enabled = request.Enabled;
        row.UpdatedAt = now;
        if (itemId is null)
        {
            db.BrandSkills.Add(row);
        }
        await db.SaveChangesAsync(ct);
        return itemId is null
            ? Results.Created($"/api/v1/brands/{id}/knowledge/skills/{row.Id}", ToResponse(row))
            : Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> DeleteSkillAsync(
        Guid id, Guid itemId, ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var removed = await db.BrandSkills.IgnoreQueryFilters()
            .Where(k => k.Id == itemId && k.BrandId == id && k.TenantId == grant.Brand.TenantId)
            .ExecuteDeleteAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> UpsertMcpServerAsync(
        Guid id, Guid? itemId, BrandMcpServerRequest request,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        Castmill.Api.Services.Secrets.ISecretCipher cipher,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return Results.Problem("MCP servers must be reached over https.", statusCode: 400);
        }
        var now = clock.GetUtcNow();
        BrandMcpServer? row = null;
        if (itemId is { } existingId)
        {
            row = await db.BrandMcpServers.IgnoreQueryFilters()
                .SingleOrDefaultAsync(k => k.Id == existingId && k.BrandId == id && k.TenantId == grant.Brand.TenantId, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
        }
        row ??= new BrandMcpServer
        {
            Id = Guid.NewGuid(), TenantId = grant.Brand.TenantId, BrandId = id,
            Name = request.Name, Url = request.Url, CreatedAt = now,
        };
        row.Name = request.Name.Trim();
        row.Url = request.Url.Trim();
        row.AllowedToolsJson = request.AllowedTools is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(request.AllowedTools.Select(t => t.Trim()).Where(t => t.Length > 0))
            : null;
        row.Enabled = request.Enabled;
        if (request.Authorization is not null)
        {
            row.AuthorizationCiphertext = request.Authorization.Length == 0 ? null : cipher.Encrypt(request.Authorization);
        }
        row.UpdatedAt = now;
        if (itemId is null)
        {
            db.BrandMcpServers.Add(row);
        }
        await db.SaveChangesAsync(ct);
        return itemId is null
            ? Results.Created($"/api/v1/brands/{id}/knowledge/mcp-servers/{row.Id}", ToResponse(row))
            : Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> DeleteMcpServerAsync(
        Guid id, Guid itemId, ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var removed = await db.BrandMcpServers.IgnoreQueryFilters()
            .Where(k => k.Id == itemId && k.BrandId == id && k.TenantId == grant.Brand.TenantId)
            .ExecuteDeleteAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>
    /// A new brand starts with the authored content briefs, not with nothing (ADR-069). The
    /// template is the primary instruction every generation reads, so a brand created and used
    /// the same afternoon was running on Castmill's generic guidance alone — which is what a
    /// weak first blog actually was.
    /// </summary>
    private static void SeedStarterTemplates(CastmillDbContext db, BrandProfile brand, DateTimeOffset now)
    {
        foreach (var kind in BrandTemplateStarters.SeededKinds)
        {
            if (BrandTemplateStarters.For(kind) is not { Length: > 0 } steering)
            {
                continue;
            }
            db.BrandTemplates.Add(new BrandTemplate
            {
                Id = Guid.NewGuid(),
                TenantId = brand.TenantId,
                BrandId = brand.Id,
                Kind = kind,
                Name = "Starter",
                SteeringPrompt = steering,
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private static BrandKnowledgeSourceResponse ToResponse(BrandKnowledgeSource k) =>
        new(k.Id, k.BrandId, k.Name, k.BaseUrl, k.QueryPath, k.QueryField, k.TokenCiphertext is not null,
            k.Enabled, k.UpdatedAt, k.ProductType, k.ProductField);

    private static BrandSkillResponse ToResponse(BrandSkill k) =>
        new(k.Id, k.BrandId, k.Name, k.FileName, k.Content, k.AppliesTo, k.Enabled, k.UpdatedAt);

    private static BrandMcpServerResponse ToResponse(BrandMcpServer k) =>
        new(k.Id, k.BrandId, k.Name, k.Url, k.AuthorizationCiphertext is not null,
            Castmill.Api.Services.Ai.BrandContextService.ParseTools(k.AllowedToolsJson), k.Enabled, k.UpdatedAt);

    private static async Task<IResult> CreateTemplateAsync(
        Guid id,
        BrandTemplateRequest request,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
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
            await db.BrandTemplates.IgnoreQueryFilters()
                .Where(t => t.BrandId == id && t.TenantId == grant.Brand.TenantId
                    && t.Kind == kind && t.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);
        }

        var template = new BrandTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = grant.Brand.TenantId,
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
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var template = await db.BrandTemplates.IgnoreQueryFilters()
            .SingleOrDefaultAsync(item => item.Id == templateId && item.BrandId == id
                && item.TenantId == grant.Brand.TenantId, ct);
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
            await db.BrandTemplates.IgnoreQueryFilters()
                .Where(t => t.BrandId == id && t.TenantId == grant.Brand.TenantId
                    && t.Kind == kind && t.IsDefault && t.Id != templateId)
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
        Guid id, Guid templateId,
        ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await FindAccessAsync(id, principal, tenant, access, tracking: false, ct);
        if (grant is null)
        {
            return Results.NotFound();
        }
        var template = await db.BrandTemplates.IgnoreQueryFilters()
            .SingleOrDefaultAsync(item => item.Id == templateId && item.BrandId == id
                && item.TenantId == grant.Brand.TenantId, ct);
        if (template is null)
        {
            return Results.NotFound();
        }

        db.BrandTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
