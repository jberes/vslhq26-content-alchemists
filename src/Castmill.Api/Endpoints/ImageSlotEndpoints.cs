using System.Data;
using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Brands;
using Castmill.Api.Services.Images;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

/// <summary>
/// B9.1–B9.3: the campaign's typed image plan (ADR-012). Slots are rows, so every
/// image counter in the UI reads from one place; generation produces variants,
/// placing one fills the slot and rewrites the manuscript stub.
/// </summary>
public static class ImageSlotEndpoints
{
    public static IEndpointRouteBuilder MapImageSlotEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns/{campaignId:guid}/image-slots")
            .RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).Validate<ImageSlotCreateRequest>().RequireRateLimiting("writes");
        group.MapPost("/reserve", ReserveAsync).RequireRateLimiting("writes");
        group.MapPost("/generate-pending", GeneratePendingAsync)
            .Validate<ImageBatchGenerateRequest>().RequireRateLimiting("ai");
        group.MapPatch("/{slotId:guid}", PatchAsync).Validate<ImageSlotPatchRequest>().RequireRateLimiting("writes");
        group.MapPost("/{slotId:guid}/generate", GenerateAsync).Validate<GenerateVariantsRequest>().RequireRateLimiting("ai");
        group.MapGet("/{slotId:guid}/prompt-preview", PromptPreviewAsync);
        group.MapPut("/{slotId:guid}/overlay", SetOverlayAsync).Validate<OverlaySpec>().RequireRateLimiting("writes");
        group.MapDelete("/{slotId:guid}/overlay", ClearOverlayAsync).RequireRateLimiting("writes");
        group.MapPost("/{slotId:guid}/variants/{variantId:guid}/edit", EditRegionAsync)
            .Validate<ImageRegionEditRequest>().RequireRateLimiting("ai");
        group.MapPost("/{slotId:guid}/place", PlaceAsync).Validate<PlaceVariantRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{slotId:guid}", ClearAsync).RequireRateLimiting("writes");
        group.MapDelete("/{slotId:guid}/variants/{variantId:guid}", DeleteVariantAsync)
            .RequireRateLimiting("writes");
        group.MapPut("/{slotId:guid}/variants/{variantId:guid}/lock", LockVariantAsync)
            .RequireRateLimiting("writes");
        group.MapDelete("/{slotId:guid}/variants/{variantId:guid}/lock", UnlockVariantAsync)
            .RequireRateLimiting("writes");

        // Persisted takes: the gallery lists them, keep/discard flips state, steer
        // makes a new take from an old one. Blobs are never deleted (immutable cache).
        group.MapGet("/{slotId:guid}/variants", ListVariantsAsync);
        group.MapGet("/{slotId:guid}/variants/{variantId:guid}/download", DownloadVariantAsync);
        group.MapPatch("/{slotId:guid}/variants/{variantId:guid}", SetVariantStateAsync)
            .Validate<VariantStateRequest>().RequireRateLimiting("writes");
        group.MapPost("/{slotId:guid}/variants/{variantId:guid}/steer", SteerAsync)
            .Validate<SteerVariantRequest>().RequireRateLimiting("ai");

        // Re-composite an already-placed image after a headline edit (ADR-013):
        // no model call, so this is a write, not an "ai" spend.
        routes.MapPost("/api/v1/images/composite", CompositeAsync)
            .Validate<CompositeHeadlineRequest>()
            .RequireAuthorization("TenantAllowed")
            .RequireRateLimiting("writes");
        return routes;
    }

    internal static ImageSlotResponse ToResponse(ImageSlot s) =>
        new(s.Id, s.CampaignId, s.Kind, s.TargetWidth, s.TargetHeight, s.Prompt, s.ModelAlias,
            s.SourceSegmentId, s.HeadlineText, s.SafeArea, s.State, s.PublishedUrl, s.BaseImageUrl, s.UpdatedAt,
            s.HeadlineBackground, s.ArtifactId, s.PromptMode,
            [.. ImageReferenceResolver.ParseIds(s.ReferenceAssetIdsJson)],
            Overlay: ParseOverlay(s.OverlaySpecJson));

    internal static OverlaySpec? ParseOverlay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var spec = System.Text.Json.JsonSerializer.Deserialize<OverlaySpec>(json, JsonWeb);
            return spec is { Boxes.Count: > 0 } ? spec : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores the overlay spec (ADR-055) and, when the slot has a placed image, re-composites
    /// it immediately — editing the overlay is free, no model call. The spec replaces the
    /// legacy single-headline composite for this slot.
    /// </summary>
    private static async Task<IResult> SetOverlayAsync(
        Guid campaignId,
        Guid slotId,
        OverlaySpec request,
        IPublicContentStore publicStore,
        IImageComposer composer,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        var now = clock.GetUtcNow();
        slot.OverlaySpecJson = request.Boxes.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(request, JsonWeb);
        slot.UpdatedAt = now;

        bool? fontFallback = null;
        if (slot.BaseImagePath is not null)
        {
            var composited = await CompositeOverlayAsync(slot, publicStore, composer, now, ct);
            if (composited is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "The slot's base image is no longer in the public container.");
            }
            fontFallback = composited.FontFallback;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { slot = ToResponse(slot), fontFallback });
    }

    private static async Task<IResult> ClearOverlayAsync(
        Guid campaignId, Guid slotId, IPublicContentStore publicStore, IImageComposer composer,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        slot.OverlaySpecJson = null;
        slot.UpdatedAt = clock.GetUtcNow();
        if (slot.BaseImagePath is not null)
        {
            // Back to the plain take (or the legacy headline composite when one is set).
            if (!string.IsNullOrWhiteSpace(slot.HeadlineText))
            {
                await CompositeSlotAsync(slot, slot.HeadlineText!, slot.SafeArea, slot.HeadlineBackground, publicStore, composer, slot.UpdatedAt, ct);
            }
            else
            {
                slot.PublishedUrl = slot.BaseImageUrl;
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(slot));
    }

    /// <summary>Renders the stored overlay spec onto the base image and publishes the result. Null when the base blob is gone.</summary>
    private static async Task<CompositeResult?> CompositeOverlayAsync(
        ImageSlot slot, IPublicContentStore publicStore, IImageComposer composer, DateTimeOffset now, CancellationToken ct)
    {
        var spec = ParseOverlay(slot.OverlaySpecJson);
        var baseBytes = await publicStore.ReadAsync(slot.BaseImagePath!, ct);
        if (baseBytes is null)
        {
            return null;
        }
        if (spec is null)
        {
            slot.PublishedUrl = slot.BaseImageUrl;
            slot.UpdatedAt = now;
            return new CompositeResult(baseBytes, false, string.Empty);
        }
        var result = composer.ComposeOverlay(baseBytes, spec);
        var url = await publicStore.PublishAsync(CompositePath(slot.CampaignId, slot.Kind), result.Image, "image/webp", ct);
        slot.PublishedUrl = url.ToString();
        slot.UpdatedAt = now;
        return result;
    }

    /// <summary>
    /// Region edit (ADR-055): repaint the masked part of an existing take. The result is a new
    /// take with lineage to the source, in the same gallery, so the producer compares before
    /// and after and keeps whichever wins.
    /// </summary>
    private static async Task<IResult> EditRegionAsync(
        Guid campaignId,
        Guid slotId,
        Guid variantId,
        ImageRegionEditRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        IImageRenderer renderer,
        IPublicContentStore publicStore,
        IImageComposer composer,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!publicStore.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for public publishing.");
        }
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        var source = await db.ImageVariants.SingleOrDefaultAsync(
            v => v.Id == variantId && v.SlotId == slotId && v.CampaignId == campaignId, ct);
        if (source is null)
        {
            return Results.NotFound();
        }
        byte[] mask;
        try
        {
            mask = Convert.FromBase64String(request.MaskPng.Contains(',', StringComparison.Ordinal)
                ? request.MaskPng[(request.MaskPng.IndexOf(',', StringComparison.Ordinal) + 1)..]
                : request.MaskPng);
        }
        catch (FormatException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["MaskPng"] = ["The mask must be base64 PNG."] });
        }
        if (RegionEdits.MaskBounds(mask) is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["MaskPng"] = ["Paint the region to change first — the mask is empty."] });
        }
        var image = await publicStore.ReadAsync(source.BlobPath, ct);
        if (image is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "The take to edit is no longer in the public container.");
        }

        var userId = AuthEndpoints.GetUserId(principal);
        var model = string.IsNullOrWhiteSpace(request.ModelAlias) ? source.Model : request.ModelAlias.Trim();
        var instruction = request.Instruction.Trim();
        return await RenderBatchAsync(
            slot, ImagePromptComposer.Steer(source.Prompt, $"[region edit] {instruction}"), request.Variants,
            steeringNote: $"edit: {instruction}", sourceVariantId: source.Id,
            principal, http, renderer, publicStore, composer, tenant, db, clock, ct,
            references: null, modelOverride: model, compareModels: null,
            renderOverride: (alias, token) => renderer.RenderEditAsync(
                userId, instruction, image, mask, slot.TargetWidth, slot.TargetHeight, alias, token));
    }

    private static async Task<IResult> ListAsync(Guid campaignId, CastmillDbContext db, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        var slots = await db.ImageSlots.Where(s => s.CampaignId == campaignId).ToListAsync(ct);
        return Results.Ok(slots
            .OrderBy(s => Array.FindIndex(ImagePlanService.Templates, t => t.Kind == s.Kind))
            .Select(ToResponse)
            .ToList());
    }

    /// <summary>
    /// Reserves the campaign-wide slots, or — with <paramref name="artifactId"/> — the
    /// per-artifact set for one blog. Blog imagery is scoped to a specific blog, so a campaign
    /// with two blogs has two headers; without this parameter the endpoint could only ever
    /// express the campaign-wide half of the plan, and a blog added after generation would
    /// have nowhere to put its images.
    /// </summary>
    private static async Task<IResult> ReserveAsync(
        Guid campaignId, string? artifactId, IImagePlanService plan, CastmillDbContext db, CancellationToken ct)
    {
        // Bound as a string, not Guid?, on purpose. A client that interpolates an unset id
        // sends "?artifactId=", and minimal-API binding turns that into a thrown
        // BadHttpRequestException rather than null — an unhandled exception in the log for
        // what plainly means "the campaign-wide set".
        Guid? target = null;
        if (!string.IsNullOrEmpty(artifactId))
        {
            if (!Guid.TryParse(artifactId, out var parsed))
            {
                return Results.Problem("artifactId must be a GUID.", statusCode: 400);
            }
            target = parsed;
        }

        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }

        // Tenant-filtered, so this also refuses another tenant's artifact id.
        if (target is { } id
            && !await db.Artifacts.AnyAsync(a => a.Id == id && a.CampaignId == campaignId, ct))
        {
            return Results.NotFound();
        }

        var slots = await plan.EnsureSlotsAsync(campaignId, ct, target);
        return Results.Ok(slots.Select(ToResponse).ToList());
    }

    private static async Task<IResult> PatchAsync(
        Guid campaignId,
        Guid slotId,
        ImageSlotPatchRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService brandAccess,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        if (request.PromptMode is not null && request.PromptMode is not ("Auto" or "Manual"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["PromptMode"] = ["Prompt mode must be Auto or Manual."],
            });
        }
        if (request.ReferenceAssetIds is { Count: > 5 })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ReferenceAssetIds"] = ["Choose at most 5 explicit reference images; up to 3 product screenshots attach automatically."],
            });
        }
        if (request.ReferenceAssetIds is { Count: > 0 } requestedReferences)
        {
            var brandId = await db.Campaigns
                .Where(c => c.Id == campaignId)
                .Select(c => c.BrandId)
                .SingleAsync(ct);
            var grant = brandId is { } id
                ? await brandAccess.FindAsync(
                    id, AuthEndpoints.GetUserId(principal), tenant.TenantId!.Value,
                    tracking: false, ct)
                : null;
            var valid = grant is not null
                ? await db.BrandAssets.IgnoreQueryFilters().CountAsync(
                    item => item.BrandId == grant.Brand.Id
                        && item.TenantId == grant.Brand.TenantId
                        && requestedReferences.Contains(item.Id), ct)
                : 0;
            if (valid != requestedReferences.Distinct().Count())
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ReferenceAssetIds"] = ["Every reference must belong to this campaign's brand kit."],
                });
            }
        }
        if (request.UseDefaultModel == true && request.ModelAlias is not null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ModelAlias"] = ["Choose a model override or use the workspace default, not both."],
            });
        }
        if (request.UseDefaultModel != true && request.ModelAlias is not null
            && string.IsNullOrWhiteSpace(request.ModelAlias))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ModelAlias"] = ["Model alias cannot be blank; use the workspace-default option instead."],
            });
        }
        slot.Prompt = request.Prompt ?? slot.Prompt;
        if (request.UseDefaultModel == true)
        {
            slot.ModelAlias = null;
        }
        else if (request.ModelAlias is not null)
        {
            slot.ModelAlias = request.ModelAlias.Trim();
        }
        slot.SourceSegmentId = request.SourceSegmentId ?? slot.SourceSegmentId;
        slot.HeadlineText = request.HeadlineText ?? slot.HeadlineText;
        slot.SafeArea = request.SafeArea ?? slot.SafeArea;
        slot.PromptMode = request.PromptMode ?? slot.PromptMode;
        if (request.ReferenceAssetIds is not null)
        {
            slot.ReferenceAssetIdsJson = System.Text.Json.JsonSerializer.Serialize(
                request.ReferenceAssetIds.Distinct().ToArray(), JsonWeb);
        }
        slot.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(slot));
    }

    private static async Task<IResult> DownloadVariantAsync(
        Guid campaignId,
        Guid slotId,
        Guid variantId,
        IPublicContentStore publicStore,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var variant = await db.ImageVariants
            .Where(item => item.CampaignId == campaignId
                && item.SlotId == slotId
                && item.Id == variantId)
            .Select(item => new { item.BlobPath })
            .SingleOrDefaultAsync(ct);
        if (variant is null)
        {
            return Results.NotFound();
        }

        var bytes = await publicStore.ReadAsync(variant.BlobPath, ct);
        return bytes is null
            ? Results.NotFound()
            : Results.File(bytes, "image/webp", $"castmill-{variantId:N}.webp");
    }

    private static async Task<IResult> CreateAsync(
        Guid campaignId,
        ImageSlotCreateRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.PromptMode is not ("Auto" or "Manual"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["PromptMode"] = ["Prompt mode must be Auto or Manual."],
            });
        }
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == request.ArtifactId && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        var sequence = await db.ImageSlots.CountAsync(
            s => s.CampaignId == campaignId && s.ArtifactId == artifact.Id
                && s.Kind.StartsWith("content-image-"), ct) + 1;
        var (defaultWidth, defaultHeight) = artifact.Kind.StartsWith("social-", StringComparison.Ordinal)
            ? (1200, 1200)
            : artifact.Kind == "youtube" ? (1280, 720) : (1200, 675);
        var now = clock.GetUtcNow();
        var slot = new ImageSlot
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            CampaignId = campaignId,
            ArtifactId = artifact.Id,
            Kind = $"content-image-{sequence}",
            TargetWidth = request.TargetWidth ?? defaultWidth,
            TargetHeight = request.TargetHeight ?? defaultHeight,
            Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt.Trim(),
            PromptMode = request.PromptMode,
            State = "Empty",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ImageSlots.Add(slot);
        await db.SaveChangesAsync(ct);
        return Results.Created(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slot.Id}", ToResponse(slot));
    }

    /// <summary>Generates N variants at the slot's exact dimensions, persists each as an
    /// <see cref="ImageVariant"/> with a gallery thumbnail, and reports per-variant
    /// progress through a pollable image run (the Press Run pattern, applied to pixels).</summary>
    private static async Task<IResult> GenerateAsync(
        Guid campaignId,
        Guid slotId,
        GenerateVariantsRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        IImageRenderer renderer,
        IImageReferenceResolver references,
        IPublicContentStore publicStore,
        IImageComposer composer,
        IBrandContextService brands,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        Castmill.Api.Services.Ai.Agents.IImageCritic critic,
        Microsoft.Extensions.Options.IOptions<Castmill.Api.Services.Ai.AiOptions> aiOptions,
        CancellationToken ct)
    {
        if (!publicStore.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for public publishing.");
        }
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        if (slot.PromptMode == "Manual" && string.IsNullOrWhiteSpace(slot.Prompt))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Prompt"] = ["This slot has no prompt yet. PATCH one, or seed it from an image-prompts artifact."],
            });
        }

        var campaign = await db.Campaigns.SingleAsync(c => c.Id == campaignId, ct);
        var brand = await brands.ResolveAsync(campaign, ct);
        var owner = slot.ArtifactId is { } artifactId
            ? await db.Artifacts.SingleOrDefaultAsync(
                a => a.Id == artifactId && a.CampaignId == campaignId, ct)
            : null;
        var resolvedReferences = await references.ResolveAsync(campaign, slot, ct);
        var effectivePrompt = ImagePromptComposer.Compose(slot, campaign, owner, brand, null, resolvedReferences);

        var compare = request.ModelAliases?
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return await RenderBatchAsync(
            slot, effectivePrompt, request.Variants, steeringNote: null, sourceVariantId: null,
            principal, http, renderer, publicStore, composer, tenant, db, clock, ct,
            resolvedReferences, request.ModelAlias,
            compare is { Length: > 0 } ? compare : null,
            critic: request.Critique ? critic : null,
            criticOptions: request.Critique ? aiOptions.Value.Agents.ImageCritic : null);
    }

    /// <summary>
    /// What a generate call would send RIGHT NOW (ADR-054), including the house composition
    /// rules the renderer appends, so the studio can show the whole text instead of a
    /// disabled textarea. Reference images are described, not resolved — resolving downloads
    /// every attached asset, which a read-only preview must not do.
    /// </summary>
    private static async Task<IResult> PromptPreviewAsync(
        Guid campaignId,
        Guid slotId,
        string? model,
        ClaimsPrincipal principal,
        IBrandContextService brands,
        IImageRenderer renderer,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        var campaign = await db.Campaigns.SingleAsync(c => c.Id == campaignId, ct);
        var brand = await brands.ResolveAsync(campaign, ct);
        var owner = slot.ArtifactId is { } artifactId
            ? await db.Artifacts.SingleOrDefaultAsync(
                a => a.Id == artifactId && a.CampaignId == campaignId, ct)
            : null;

        // Mirrors ImageReferenceResolver's selection (explicit picks + the brand's product
        // screenshots) as a yes/no, without downloading a byte.
        var referencesAttach = campaign.BrandId is { } brandId
            && (!string.IsNullOrWhiteSpace(slot.ReferenceAssetIdsJson) && slot.ReferenceAssetIdsJson.Trim() is not ("[]" or "null")
                || await db.BrandAssets.IgnoreQueryFilters()
                    .AnyAsync(asset => asset.BrandId == brandId && asset.Kind == "product", ct));

        var prompt = slot.PromptMode == "Manual" && string.IsNullOrWhiteSpace(slot.Prompt)
            ? string.Empty
            : ImagePromptComposer.Compose(slot, campaign, owner, brand, null,
                referencesAttach ? [new ImageReference(Guid.Empty, "reference", "image/png", [], "product")] : null);

        // The frame is the PROVIDER's: MAI paints the slot's exact size, Gemini a native 16:9
        // frame, gpt-image one of three fixed sizes — so the crop, and the rules text that
        // describes it, differ per model (ADR-055).
        var modelAlias = string.IsNullOrWhiteSpace(model) ? slot.ModelAlias : model.Trim();
        var frame = await renderer.FrameForAsync(AuthEndpoints.GetUserId(principal), slot.TargetWidth, slot.TargetHeight, modelAlias, ct);
        if (prompt.Length > 0)
        {
            prompt = ImagePromptRules.Apply(prompt, slot.TargetWidth, slot.TargetHeight, frame.Width, frame.Height);
        }

        var targetAspect = (double)slot.TargetWidth / slot.TargetHeight;
        var frameAspect = (double)frame.Width / frame.Height;
        var horizontal = targetAspect < frameAspect ? (1 - (targetAspect / frameAspect)) * 50 : 0;
        var vertical = targetAspect > frameAspect ? (1 - (frameAspect / targetAspect)) * 50 : 0;

        return Results.Ok(new ImagePromptPreviewResponse(
            prompt, slot.PromptMode, slot.TargetWidth, slot.TargetHeight,
            frame.Width, frame.Height,
            Math.Round(horizontal, 1), Math.Round(vertical, 1), referencesAttach));
    }

    private static async Task<IResult> GeneratePendingAsync(
        Guid campaignId,
        ImageBatchGenerateRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        IImageRenderer renderer,
        IImageReferenceResolver references,
        IImageProviderRegistry providers,
        IFoundryClientFactory foundry,
        IPublicContentStore publicStore,
        IImageComposer composer,
        IBrandContextService brands,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken requestCt)
    {
        if (!publicStore.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for public publishing.");
        }

        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, requestCt);
        if (campaign is null)
        {
            return Results.NotFound();
        }
        if (request.ArtifactId is { } artifactId
            && !await db.Artifacts.AnyAsync(
                artifact => artifact.Id == artifactId && artifact.CampaignId == campaignId, requestCt))
        {
            return Results.NotFound();
        }

        var userId = AuthEndpoints.GetUserId(principal);
        var workspaceDefaultModel = await db.UserSettings
            .Where(setting => setting.UserId == userId
                && setting.Key == "images.default-model" && !setting.IsEncrypted)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(requestCt);

        var activeBatch = await db.GenerationRuns
            .Where(run => run.CampaignId == campaignId
                && run.Kind == "image-batch" && run.Status == "Running")
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefaultAsync(requestCt);
        if (activeBatch is not null)
        {
            http.Response.Headers.Append("Castmill-Run-Id", activeBatch.Id.ToString());
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "This campaign already has an image batch running. Reattach to the existing run.");
        }

        var slots = await db.ImageSlots
            .Where(slot => slot.CampaignId == campaignId
                && (request.ArtifactId == null || slot.ArtifactId == request.ArtifactId))
            .ToListAsync(requestCt);
        slots = [.. slots
            .OrderBy(slot => slot.ArtifactId ?? Guid.Empty)
            .ThenBy(slot => Array.FindIndex(ImagePlanService.Templates, template => template.Kind == slot.Kind)
                is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(slot => slot.CreatedAt)
            .ThenBy(slot => slot.Id)];

        var renderingSlotIds = await db.GenerationRuns
            .Where(run => run.CampaignId == campaignId && run.Kind == "image"
                && run.Status == "Running" && run.SlotId != null)
            .Select(run => run.SlotId!.Value)
            .ToListAsync(requestCt);
        var rendering = renderingSlotIds.ToHashSet();
        var activeTakeCounts = await db.ImageVariants
            .Where(variant => variant.CampaignId == campaignId && variant.State != "Discarded")
            .GroupBy(variant => variant.SlotId)
            .Select(group => new { SlotId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.SlotId, row => row.Count, requestCt);
        var skipped = new List<ImageBatchSlotResult>();
        var eligible = new List<(ImageSlot Slot, int Variants)>();
        foreach (var slot in slots)
        {
            if (slot.State == "Filled")
            {
                skipped.Add(Skipped(slot, request.VariantsPerSlot, "already_filled",
                    "The slot already has a placed image."));
            }
            else if (rendering.Contains(slot.Id))
            {
                skipped.Add(Skipped(slot, request.VariantsPerSlot, "already_rendering",
                    "The slot already has a generation run in progress."));
            }
            else if (slot.PromptMode == "Manual" && string.IsNullOrWhiteSpace(slot.Prompt))
            {
                skipped.Add(Skipped(slot, request.VariantsPerSlot, "manual_prompt_missing",
                    "The manual slot has no prompt."));
            }
            else if (activeTakeCounts.GetValueOrDefault(slot.Id) >= request.VariantsPerSlot)
            {
                skipped.Add(Skipped(slot, request.VariantsPerSlot, "take_target_met",
                    "The slot already has the requested number of active takes."));
            }
            else
            {
                eligible.Add((slot,
                    request.VariantsPerSlot - activeTakeCounts.GetValueOrDefault(slot.Id)));
            }
        }

        var brand = await brands.ResolveAsync(campaign, requestCt);
        var owners = await db.Artifacts
            .Where(artifact => artifact.CampaignId == campaignId)
            .ToDictionaryAsync(artifact => artifact.Id, requestCt);
        var prepared = new List<PreparedImageBatchSlot>(eligible.Count);
        foreach (var work in eligible)
        {
            var slot = work.Slot;
            var effectiveModel = string.IsNullOrWhiteSpace(slot.ModelAlias)
                ? workspaceDefaultModel
                : slot.ModelAlias;
            try
            {
                var resolvedReferences = await references.ResolveAsync(campaign, slot, requestCt);
                var provider = providers.Resolve(effectiveModel);
                var readiness = await provider.StatusAsync(userId, requestCt);
                if (!readiness.Ready)
                {
                    skipped.Add(Skipped(slot, work.Variants, "provider_unavailable",
                        readiness.Reason ?? "The image provider is not ready."));
                    continue;
                }
                if (provider.Name == "foundry")
                {
                    var alias = string.IsNullOrWhiteSpace(effectiveModel)
                        || effectiveModel.Equals("foundry", StringComparison.OrdinalIgnoreCase)
                        ? "image"
                        : effectiveModel;
                    if (await foundry.ResolveTargetAsync(userId, alias, requestCt) is null)
                    {
                        skipped.Add(Skipped(slot, work.Variants, "model_unavailable",
                            $"The Foundry model alias '{alias}' is not configured for this user."));
                        continue;
                    }
                }
                if (resolvedReferences.Count > 0 && !readiness.SupportsReferenceImages)
                {
                    skipped.Add(Skipped(slot, work.Variants, "reference_unsupported",
                        "The selected image model cannot use this slot's required reference images."));
                    continue;
                }

                var owner = slot.ArtifactId is { } ownerId
                    && owners.TryGetValue(ownerId, out var artifact) ? artifact : null;
                var effectivePrompt = ImagePromptComposer.Compose(slot, campaign, owner, brand, null, resolvedReferences);
                prepared.Add(new PreparedImageBatchSlot(
                    slot, work.Variants, effectiveModel, effectivePrompt, resolvedReferences));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !requestCt.IsCancellationRequested)
            {
                skipped.Add(Skipped(slot, work.Variants, FailureCode(ex), FailureReason(ex)));
            }
        }

        var preparedSeed = prepared.ToList();
        var skippedSeed = skipped.ToList();
        GenerationRun? run = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            activeBatch = null;
            run = null;
            var attemptPrepared = preparedSeed.ToList();
            var attemptSkipped = skippedSeed.ToList();
            db.ChangeTracker.Clear();
            await using var startLock = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, requestCt);
            await CampaignEndpoints.AcquireCampaignLockAsync(db, campaignId, requestCt);
            activeBatch = await db.GenerationRuns
                .Where(candidate => candidate.CampaignId == campaignId
                    && candidate.Kind == "image-batch" && candidate.Status == "Running")
                .OrderByDescending(candidate => candidate.StartedAt)
                .FirstOrDefaultAsync(requestCt);
            if (activeBatch is not null)
            {
                return;
            }

            var preparedIds = attemptPrepared.Select(work => work.Slot.Id).ToList();
            var currentStates = await db.ImageSlots
                .Where(candidate => preparedIds.Contains(candidate.Id))
                .Select(candidate => new { candidate.Id, candidate.State })
                .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.State, requestCt);
            var currentRendering = (await db.GenerationRuns
                    .Where(candidate => candidate.CampaignId == campaignId && candidate.Kind == "image"
                        && candidate.Status == "Running" && candidate.SlotId != null
                        && preparedIds.Contains(candidate.SlotId.Value))
                    .Select(candidate => candidate.SlotId!.Value)
                    .ToListAsync(requestCt))
                .ToHashSet();
            var currentTakeCounts = await db.ImageVariants
                .Where(variant => preparedIds.Contains(variant.SlotId) && variant.State != "Discarded")
                .GroupBy(variant => variant.SlotId)
                .Select(group => new { SlotId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.SlotId, row => row.Count, requestCt);
            for (var index = attemptPrepared.Count - 1; index >= 0; index--)
            {
                var work = attemptPrepared[index];
                if (currentStates.GetValueOrDefault(work.Slot.Id) == "Filled")
                {
                    attemptSkipped.Add(Skipped(work.Slot, work.Variants, "already_filled",
                        "The slot was filled while the batch was preparing."));
                    attemptPrepared.RemoveAt(index);
                    continue;
                }
                if (currentRendering.Contains(work.Slot.Id))
                {
                    attemptSkipped.Add(Skipped(work.Slot, work.Variants, "already_rendering",
                        "The slot started another generation run while the batch was preparing."));
                    attemptPrepared.RemoveAt(index);
                    continue;
                }

                var remaining = request.VariantsPerSlot - currentTakeCounts.GetValueOrDefault(work.Slot.Id);
                if (remaining <= 0)
                {
                    attemptSkipped.Add(Skipped(work.Slot, work.Variants, "take_target_met",
                        "The slot reached the requested take target while the batch was preparing."));
                    attemptPrepared.RemoveAt(index);
                }
                else if (remaining != work.Variants)
                {
                    attemptPrepared[index] = work with { Variants = remaining };
                }
            }

            var now = clock.GetUtcNow();
            run = new GenerationRun
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                CampaignId = campaignId,
                Status = "Running",
                Kind = "image-batch",
                SlotId = null,
                TotalKinds = attemptSkipped.Count + attemptPrepared.Sum(item => item.Variants),
                ItemsJson = "[]",
                StartedAt = now,
                UpdatedAt = now,
            };
            db.GenerationRuns.Add(run);
            await db.SaveChangesAsync(requestCt);
            await startLock.CommitAsync(requestCt);
            prepared = attemptPrepared;
            skipped = attemptSkipped;
        });
        if (activeBatch is not null)
        {
            http.Response.Headers.Append("Castmill-Run-Id", activeBatch.Id.ToString());
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "This campaign already has an image batch running. Reattach to the existing run.");
        }
        if (run is null)
        {
            throw new InvalidOperationException("The image batch did not start.");
        }
        http.Response.Headers.Append("Castmill-Run-Id", run.Id.ToString());

        var events = new List<object>();
        var results = new List<ImageBatchSlotResult>(slots.Count);
        foreach (var skip in skipped)
        {
            results.Add(skip);
            events.Add(BatchEvent(skip, variantIndex: null, success: false, durationMs: 0));
        }
        await SaveBatchProgressAsync(db, run, events, clock, CancellationToken.None);

        foreach (var work in prepared)
        {
            var slot = work.Slot;
            var succeeded = 0;
            var failures = new List<(string Code, string Message)>();

            for (var variantIndex = 1; variantIndex <= work.Variants; variantIndex++)
            {
                var activeEvent = BatchGeneratingEvent(slot, variantIndex);
                events.Add(activeEvent);
                await SaveBatchProgressAsync(db, run, events, clock, CancellationToken.None);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var webp = await renderer.RenderExactAsync(
                        userId, work.EffectivePrompt, slot.TargetWidth, slot.TargetHeight,
                        work.EffectiveModel, work.References, CancellationToken.None);
                    var thumb = composer.ToThumbWebp(webp);
                    var blobPath = VariantPath(slot.CampaignId, slot.Kind, variantIndex);
                    var thumbPath = ThumbPath(slot.CampaignId, slot.Kind);
                    var url = await publicStore.PublishAsync(
                        blobPath, webp, "image/webp", CancellationToken.None);
                    var thumbUrl = await publicStore.PublishAsync(
                        thumbPath, thumb, "image/webp", CancellationToken.None);
                    db.ImageVariants.Add(new ImageVariant
                    {
                        Id = Guid.NewGuid(),
                        TenantId = slot.TenantId,
                        CampaignId = slot.CampaignId,
                        SlotId = slot.Id,
                        Url = url.ToString(),
                        BlobPath = blobPath,
                        ThumbUrl = thumbUrl.ToString(),
                        ThumbBlobPath = thumbPath,
                        Model = work.EffectiveModel ?? "image",
                        Prompt = work.EffectivePrompt,
                        State = "Candidate",
                        Width = slot.TargetWidth,
                        Height = slot.TargetHeight,
                        CreatedAt = clock.GetUtcNow(),
                    });
                    succeeded++;
                    events.Add(BatchEvent(slot, variantIndex, success: true, null, null,
                        stopwatch.ElapsedMilliseconds));
                }
                catch (Exception ex)
                {
                    var failure = (FailureCode(ex), FailureReason(ex));
                    failures.Add(failure);
                    events.Add(BatchEvent(slot, variantIndex, success: false,
                        failure.Item1, failure.Item2, stopwatch.ElapsedMilliseconds));
                }
                    events.Remove(activeEvent);
                await SaveBatchProgressAsync(db, run, events, clock, CancellationToken.None);
            }

            var failed = work.Variants - succeeded;
            var firstFailure = failures.FirstOrDefault();
            results.Add(new ImageBatchSlotResult(
                slot.Id, slot.Kind,
                failed == 0 ? "Succeeded" : succeeded == 0 ? "Failed" : "Partial",
                work.Variants, succeeded, failed,
                firstFailure == default ? null : firstFailure.Code,
                firstFailure == default ? null : firstFailure.Message));
        }

        run.Status = "Completed";
        await SaveBatchProgressAsync(db, run, events, clock, CancellationToken.None);
        return Results.Ok(new ImageBatchResponse(
            run.Id,
            prepared.Count,
            results.Count(result => result.Outcome == "Succeeded"),
            results.Count(result => result.Outcome is "Failed" or "Partial"),
            results.Count(result => result.Outcome == "Skipped"),
            results.Sum(result => result.SucceededVariants),
            results.Sum(result => result.FailedVariants),
            results));
    }

    private sealed record PreparedImageBatchSlot(
        ImageSlot Slot,
        int Variants,
        string? EffectiveModel,
        string EffectivePrompt,
        IReadOnlyList<ImageReference> References);

    private static ImageBatchSlotResult Skipped(
        ImageSlot slot, int requestedVariants, string code, string message) =>
        new(slot.Id, slot.Kind, "Skipped", requestedVariants, 0, 0, code, message);

    private static object BatchEvent(
        ImageBatchSlotResult result, int? variantIndex, bool success, long durationMs) => new
        {
            kind = result.Kind,
            slotId = result.SlotId,
            variantIndex,
            success,
            outcome = result.Outcome,
            errorCode = result.ErrorCode,
            error = result.Error,
            durationMs,
        };

    private static object BatchEvent(
        ImageSlot slot, int variantIndex, bool success,
        string? errorCode, string? error, long durationMs) => new
        {
            kind = slot.Kind,
            slotId = slot.Id,
            variantIndex,
            success,
            outcome = success ? "Succeeded" : "Failed",
            errorCode,
            error,
            durationMs,
        };

    private static object BatchGeneratingEvent(ImageSlot slot, int variantIndex) => new
    {
        kind = slot.Kind,
        slotId = slot.Id,
        variantIndex,
        success = false,
        outcome = "Generating",
        errorCode = (string?)null,
        error = (string?)null,
        durationMs = 0L,
    };

    private static async Task SaveBatchProgressAsync(
        CastmillDbContext db, GenerationRun run, List<object> events,
        TimeProvider clock, CancellationToken ct)
    {
        run.ItemsJson = System.Text.Json.JsonSerializer.Serialize(events, JsonWeb);
        run.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static string FailureCode(Exception ex) => ex switch
    {
        ImageModerationException => "moderation_refusal",
        AiNotConfiguredException => "provider_unavailable",
        ImageProviderException => "provider_error",
        TaskCanceledException or TimeoutException => "provider_timeout",
        HttpRequestException => "provider_network_error",
        _ => "render_failed",
    };

    /// <summary>
    /// Hard delete: the row AND its blobs. Discard is the soft, recoverable path; this is
    /// for a take the user never wants to see again. The take whose blob is the slot's
    /// current base image is refused — the published image and overlay re-compositing
    /// read that blob, so it must be removed from the slot first.
    /// </summary>
    private static async Task<IResult> DeleteVariantAsync(
        Guid campaignId, Guid slotId, Guid variantId,
        IPublicContentStore publicStore, CastmillDbContext db, CancellationToken ct)
    {
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        var variant = await db.ImageVariants.AsNoTracking().SingleOrDefaultAsync(
            v => v.Id == variantId && v.SlotId == slotId && v.CampaignId == campaignId, ct);
        if (variant is null)
        {
            return Results.NotFound();
        }
        if (slot.State == "Filled" && slot.BaseImagePath == variant.BlobPath)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "This take is the image currently placed in the slot. "
                    + "Remove it from the slot first, then delete it.");
        }

        var deleted = await db.ImageVariants
            .Where(item => item.Id == variantId
                && item.SlotId == slotId
                && item.CampaignId == campaignId
                && item.LockedByUserId == null)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return await db.ImageVariants.AnyAsync(
                item => item.Id == variantId && item.SlotId == slotId && item.CampaignId == campaignId, ct)
                ? Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "This take is locked. Unlock it before deleting it.")
                : Results.NotFound();
        }

        // Blobs after the row: repeating a delete for a missing blob is harmless,
        // resurrecting a row because a blob call hiccupped is not.
        if (publicStore.IsConfigured)
        {
            await publicStore.DeleteAsync(variant.BlobPath, ct);
            if (!string.IsNullOrEmpty(variant.ThumbBlobPath))
            {
                await publicStore.DeleteAsync(variant.ThumbBlobPath, ct);
            }
        }
        return Results.NoContent();
    }

    private static async Task<IResult> LockVariantAsync(
        Guid campaignId,
        Guid slotId,
        Guid variantId,
        ClaimsPrincipal principal,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LoadSlotAsync(campaignId, slotId, db, ct) is null)
        {
            return Results.NotFound();
        }

        var userId = AuthEndpoints.GetUserId(principal);
        var lockedAt = clock.GetUtcNow();
        var updated = await db.ImageVariants
            .Where(item => item.Id == variantId
                && item.SlotId == slotId
                && item.CampaignId == campaignId
                && (item.LockedByUserId == null || item.LockedByUserId == userId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LockedByUserId, userId)
                .SetProperty(item => item.LockedAt, lockedAt), ct);
        if (updated == 0)
        {
            return await db.ImageVariants.AnyAsync(
                item => item.Id == variantId && item.SlotId == slotId && item.CampaignId == campaignId, ct)
                ? Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "This take is already locked by another collaborator.")
                : Results.NotFound();
        }

        var variant = await db.ImageVariants.AsNoTracking().SingleAsync(
            item => item.Id == variantId && item.SlotId == slotId && item.CampaignId == campaignId, ct);
        return Results.Ok(ToResponse(variant, userId, userId));
    }

    private static async Task<IResult> UnlockVariantAsync(
        Guid campaignId,
        Guid slotId,
        Guid variantId,
        ClaimsPrincipal principal,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (await LoadSlotAsync(campaignId, slotId, db, ct) is null)
        {
            return Results.NotFound();
        }

        var userId = AuthEndpoints.GetUserId(principal);
        var ownerId = await db.Campaigns
            .Where(campaign => campaign.Id == campaignId)
            .Select(campaign => campaign.OwnerId)
            .SingleAsync(ct);
        var updated = await db.ImageVariants
            .Where(item => item.Id == variantId
                && item.SlotId == slotId
                && item.CampaignId == campaignId
                && item.LockedByUserId != null
                && (item.LockedByUserId == userId || ownerId == userId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LockedByUserId, (Guid?)null)
                .SetProperty(item => item.LockedAt, (DateTimeOffset?)null), ct);
        if (updated == 1)
        {
            return Results.NoContent();
        }

        var variant = await db.ImageVariants.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == variantId && item.SlotId == slotId && item.CampaignId == campaignId, ct);
        if (variant is null)
        {
            return Results.NotFound();
        }
        if (variant.LockedByUserId is not null)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                detail: "Only the person who locked this take or the campaign owner can unlock it.");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ListVariantsAsync(
        Guid campaignId, Guid slotId, ClaimsPrincipal principal, CastmillDbContext db, CancellationToken ct,
        bool includeDiscarded = false)
    {
        if (await LoadSlotAsync(campaignId, slotId, db, ct) is null)
        {
            return Results.NotFound();
        }

        var variants = await db.ImageVariants
            .Where(v => v.SlotId == slotId && (includeDiscarded || v.State != "Discarded"))
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
        var ownerId = await db.Campaigns
            .Where(campaign => campaign.Id == campaignId)
            .Select(campaign => campaign.OwnerId)
            .SingleAsync(ct);
        var userId = AuthEndpoints.GetUserId(principal);
        return Results.Ok(variants.Select(variant => ToResponse(variant, userId, ownerId)).ToList());
    }

    private static async Task<IResult> SetVariantStateAsync(
        Guid campaignId,
        Guid slotId,
        Guid variantId,
        VariantStateRequest request,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (request.State is not ("Candidate" or "Kept" or "Discarded"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["State"] = ["State must be Candidate, Kept or Discarded."],
            });
        }

        var variant = await db.ImageVariants.SingleOrDefaultAsync(
            v => v.Id == variantId && v.SlotId == slotId && v.CampaignId == campaignId, ct);
        if (variant is null)
        {
            return Results.NotFound();
        }

        // A state flip only — the blob stays (immutable cache, and placed slots may
        // reference it). "Throwaway" means gone from the gallery, not gone from disk.
        variant.State = request.State;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(variant));
    }

    /// <summary>New take(s) steered from an existing one: the original take's EXACT prompt
    /// plus the adjustment note. v1 steers by prompt (the providers are text-to-image);
    /// true image-to-image edits are a provider-seam extension.</summary>
    private static async Task<IResult> SteerAsync(
        Guid campaignId,
        Guid slotId,
        Guid variantId,
        SteerVariantRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        IImageRenderer renderer,
        IImageReferenceResolver references,
        IPublicContentStore publicStore,
        IImageComposer composer,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!publicStore.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for public publishing.");
        }
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        var source = await db.ImageVariants.SingleOrDefaultAsync(
            v => v.Id == variantId && v.SlotId == slotId && v.CampaignId == campaignId, ct);
        if (source is null)
        {
            return Results.NotFound();
        }

        // The source's persisted prompt already carries brand steering — append only
        // the adjustment, so lineage is honest and reproducible. The note is optional:
        // steering by reference alone (ADR-025's real image inputs) is a complete request.
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var campaign = await db.Campaigns.SingleAsync(c => c.Id == campaignId, ct);
        var resolvedReferences = await references.ResolveAsync(campaign, slot, ct);
        var effectivePrompt = ImagePromptComposer.Steer(source.Prompt, note, resolvedReferences);

        return await RenderBatchAsync(
            slot, effectivePrompt, request.Variants, note, source.Id,
            principal, http, renderer, publicStore, composer, tenant, db, clock, ct,
            resolvedReferences, request.ModelAlias);
    }

    /// <summary>The shared render loop for generate and steer: run row first, then one
    /// persisted variant + thumb per render, progress recorded per completion.</summary>
    private static async Task<IResult> RenderBatchAsync(
        ImageSlot slot,
        string effectivePrompt,
        int count,
        string? steeringNote,
        Guid? sourceVariantId,
        ClaimsPrincipal principal,
        HttpContext http,
        IImageRenderer renderer,
        IPublicContentStore publicStore,
        IImageComposer composer,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct,
        IReadOnlyList<ImageReference>? references = null,
        string? modelOverride = null,
        IReadOnlyList<string>? compareModels = null,
        Func<string?, CancellationToken, Task<byte[]>>? renderOverride = null,
        Castmill.Api.Services.Ai.Agents.IImageCritic? critic = null,
        Castmill.Api.Services.Ai.AiOptions.ImageCriticOptions? criticOptions = null)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        // Compare mode (ADR-054): the same prompt on every listed model, `count` takes each.
        // One run row, one gallery, every take labelled with the model that painted it.
        List<string?> jobs = compareModels is { Count: > 0 }
            ? compareModels.SelectMany(alias => Enumerable.Range(1, count).Select(_ => (string?)alias)).ToList()
            : Enumerable.Repeat(string.IsNullOrWhiteSpace(modelOverride) ? slot.ModelAlias : modelOverride.Trim(), count)
                .ToList();
        var comparing = jobs.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        count = jobs.Count;
        GenerationRun? run = null;
        IResult? startError = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            run = null;
            startError = null;
            db.ChangeTracker.Clear();
            await using var startLock = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, ct);
            await CampaignEndpoints.AcquireCampaignLockAsync(db, slot.CampaignId, ct);
            if (await db.GenerationRuns.AnyAsync(candidate => candidate.CampaignId == slot.CampaignId
                && candidate.Kind == "image-batch" && candidate.Status == "Running", ct))
            {
                startError = Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "This campaign has a generate-all-pending pass in progress. Reattach to that run before starting another take.");
                return;
            }
            var now = clock.GetUtcNow();
            run = new GenerationRun
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                CampaignId = slot.CampaignId,
                Status = "Running",
                Kind = "image",
                SlotId = slot.Id,
                TotalKinds = count,
                ItemsJson = "[]",
                StartedAt = now,
                UpdatedAt = now,
            };
            db.GenerationRuns.Add(run);
            await db.SaveChangesAsync(ct);
            await startLock.CommitAsync(ct);
        });
        if (startError is not null)
        {
            return startError;
        }

        if (run is null)
        {
            throw new InvalidOperationException("The image run did not start.");
        }
        http.Response.Headers.Append("Castmill-Run-Id", run.Id.ToString());

        var variants = new List<ImageVariantResponse>();
        var failures = new List<string>();
        var items = new List<object>();

        // Renders run concurrently across models (a compare batch would otherwise take
        // N × one render); persistence stays on this thread because DbContext is not
        // thread-safe. Each render is awaited in completion order so progress stays live.
        var pending = jobs.Select((model, index) => RenderOneAsync(index + 1, model)).ToList();
        async Task<(int Take, string? Model, byte[]? Webp, Exception? Error, long DurationMs, string? Note, string Prompt)> RenderOneAsync(int take, string? model)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                byte[] webp;
                // The prompt stored on the take is the one the provider was given, rules and
                // all (ADR-065) — a producer diagnosing a bad image needs the real text.
                var sentPrompt = effectivePrompt;
                if (renderOverride is not null)
                {
                    webp = await renderOverride(model, ct);
                }
                else
                {
                    (webp, sentPrompt) = await renderer.RenderExactReportingPromptAsync(
                        userId, effectivePrompt, slot.TargetWidth, slot.TargetHeight, model,
                        references ?? [], ct);
                }

                // Art-director loop (ADR-058): judge the take against the brief; on a rejection
                // re-render ONCE per round with the named defect, keeping the best-scoring take.
                string? note = null;
                if (critic is not null && criticOptions is { Enabled: true } && renderOverride is null)
                {
                    var verdict = await critic.ReviewAsync(userId, effectivePrompt, slot.Kind, webp, ct);
                    var best = (Webp: webp, verdict.Score);
                    var rounds = 0;
                    while (verdict.Ran && !verdict.Accept && rounds < criticOptions.MaxRounds && verdict.Fix is { Length: > 0 } fix)
                    {
                        rounds++;
                        var retryPrompt = $"{effectivePrompt}\nArt director's fix for the previous render: {fix}";
                        var (retry, retrySent) = await renderer.RenderExactReportingPromptAsync(
                            userId, retryPrompt, slot.TargetWidth, slot.TargetHeight, model, references ?? [], ct);
                        verdict = await critic.ReviewAsync(userId, effectivePrompt, slot.Kind, retry, ct);
                        if (!verdict.Ran || verdict.Score >= best.Score)
                        {
                            best = (retry, verdict.Score);
                            sentPrompt = retrySent;
                        }
                    }
                    webp = best.Webp;
                    note = verdict.Ran
                        ? $"{verdict.Summary}{(rounds > 0 ? $" · {rounds} re-render{(rounds == 1 ? "" : "s")}" : "")}"
                        : verdict.Summary;
                }
                return (take, model, webp, null, stopwatch.ElapsedMilliseconds, note, sentPrompt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (take, model, null, ex, stopwatch.ElapsedMilliseconds, null, effectivePrompt);
            }
        }

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            var (i, model, rendered, error, durationMs, criticNote, sentPrompt) = await finished;
            // The batch's model, recorded on every variant so a gallery of takes from two
            // models stays readable.
            try
            {
                if (error is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                }
                var webp = rendered!;
                var thumb = composer.ToThumbWebp(webp);

                // Unique names: published blobs carry immutable cache headers, so a
                // variant must never reuse a path a previous one held.
                var blobPath = VariantPath(slot.CampaignId, slot.Kind, i);
                var thumbPath = ThumbPath(slot.CampaignId, slot.Kind);
                var url = await publicStore.PublishAsync(blobPath, webp, "image/webp", ct);
                var thumbUrl = await publicStore.PublishAsync(thumbPath, thumb, "image/webp", ct);

                var variant = new ImageVariant
                {
                    Id = Guid.NewGuid(),
                    TenantId = slot.TenantId,
                    CampaignId = slot.CampaignId,
                    SlotId = slot.Id,
                    Url = url.ToString(),
                    BlobPath = blobPath,
                    ThumbUrl = thumbUrl.ToString(),
                    ThumbBlobPath = thumbPath,
                    Model = model ?? "image",
                    Prompt = sentPrompt,
                    SteeringNote = criticNote is null ? steeringNote : (steeringNote is null ? criticNote : $"{steeringNote} · {criticNote}"),
                    SourceVariantId = sourceVariantId,
                    State = "Candidate",
                    Width = slot.TargetWidth,
                    Height = slot.TargetHeight,
                    CreatedAt = clock.GetUtcNow(),
                };
                db.ImageVariants.Add(variant);
                variants.Add(ToResponse(variant));
                items.Add(new { kind = $"v{i}", model, success = true, durationMs, critic = criticNote });
            }
            catch (AiNotConfiguredException ex) when (!comparing)
            {
                await CompleteImageRunAsync(db, run, items, clock, ct);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Two rules, both learned the hard way:
                //
                // 1. The filter admits a provider-side TaskCanceledException (an HTTP timeout,
                //    which IS an OperationCanceledException). Those used to escape this loop
                //    and 500 the whole batch, leaving the run row stuck "Running" — while a
                //    genuine client disconnect (ct signalled) still propagates as before.
                // 2. The reason the client sees is the exception MESSAGE, not its type name.
                //    A type name cannot be acted on: "InvalidOperationException" hid a
                //    deterministic 400 ("this model does not support 'input_fidelity'") for
                //    long enough to be re-diagnosed from scratch five times.
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Castmill.ImageSlots")
                    .LogError(ex, "Image render v{Take}/{Count} for slot {SlotId} failed", i, count, slot.Id);
                var reason = FailureReason(ex);
                var label = comparing ? $"{model} v{i}" : $"v{i}";
                failures.Add($"{label}: {reason}"); // partial failure never sinks the set
                items.Add(new { kind = $"v{i}", model, success = false, error = reason, durationMs });
            }

            run.ItemsJson = System.Text.Json.JsonSerializer.Serialize(items, JsonWeb);
            run.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct); // per-variant progress, pollable from any instance
        }

        await CompleteImageRunAsync(db, run, items, clock, ct);
        return Results.Ok(new VariantBatchResponse(run.Id, slot.Id, slot.Kind, variants, failures));
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonWeb =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// What a failed take tells the producer. Our own provider exceptions carry curated,
    /// already-sanitised sentences (they never quote a request body, which could contain a
    /// credential), so they pass through verbatim. Anything else is summarised without
    /// leaking internals — but it is always logged in full first.
    /// </summary>
    internal const string TimedOutReason =
        "The provider did not answer in time. Try again, or generate fewer takes at once.";

    internal static string FailureReason(Exception ex) => ex switch
    {
        ImageModerationException or ImageProviderException or AiNotConfiguredException => ex.Message,
        TaskCanceledException or TimeoutException => TimedOutReason,
        HttpRequestException => "Could not reach the image provider (network or DNS failure). Try again.",
        InvalidOperationException when ex.Message.Length is > 0 and < 400 => ex.Message,
        // Polly's own timeout, matched by name so this endpoint needn't take a dependency
        // on the resilience package just to phrase one sentence.
        _ when ex.GetType().Name == "TimeoutRejectedException" => TimedOutReason,
        _ => $"Unexpected {ex.GetType().Name} — see the server log for this run.",
    };

    private static async Task CompleteImageRunAsync(
        CastmillDbContext db, GenerationRun run, List<object> items, TimeProvider clock, CancellationToken ct)
    {
        run.ItemsJson = System.Text.Json.JsonSerializer.Serialize(items, JsonWeb);
        run.Status = "Completed";
        run.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Forwarder kept for callers/tests; the composer owns the digest (ADR-055).</summary>
    internal static string? ContentDigest(string? contentJson, int maxChars = 1800) =>
        ImagePromptComposer.ContentDigest(contentJson, maxChars);

    internal static ImageVariantResponse ToResponse(
        ImageVariant v, Guid? userId = null, Guid? campaignOwnerId = null) =>
        new(v.Id, v.SlotId, v.Url, v.ThumbUrl, v.Model, v.State,
            v.SteeringNote, v.SourceVariantId, v.Width, v.Height, v.CreatedAt,
            v.LockedByUserId is not null,
            v.LockedByUserId is not null
                && (v.LockedByUserId == userId || campaignOwnerId == userId),
            v.LockedAt,
            v.Prompt);

    /// <summary>
    /// Places a chosen variant: the slot flips Filled, the headline (if any) is
    /// composited server-side, and the blog's ![stub:kind]() marker is replaced.
    /// </summary>
    private static async Task<IResult> PlaceAsync(
        Guid campaignId,
        Guid slotId,
        PlaceVariantRequest request,
        IPublicContentStore publicStore,
        IImageComposer composer,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }

        string? basePath;
        string? baseUrl;
        if (request.VariantId is { } variantId)
        {
            // Preferred path: the persisted row is authoritative — no URL parsing, and
            // placing a take is what "keep" means.
            var variant = await db.ImageVariants.SingleOrDefaultAsync(
                v => v.Id == variantId && v.SlotId == slotId && v.CampaignId == campaignId, ct);
            if (variant is null)
            {
                return Results.NotFound();
            }
            variant.State = "Kept";
            basePath = variant.BlobPath;
            baseUrl = variant.Url;
        }
        else if (request.Url is { Length: > 0 } url)
        {
            basePath = PathFromVariantUrl(url, campaignId, slot.Kind);
            baseUrl = url;
        }
        else
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["VariantId"] = ["Provide the variant id to place (or, transitionally, its URL)."],
            });
        }

        if (basePath is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Url"] = ["The URL is not a variant published for this slot."],
            });
        }

        var now = clock.GetUtcNow();
        slot.BaseImagePath = basePath;
        slot.BaseImageUrl = baseUrl;
        slot.PublishedUrl = baseUrl;
        slot.State = "Filled";
        slot.UpdatedAt = now;

        bool? fontFallback = null;
        if (ParseOverlay(slot.OverlaySpecJson) is not null)
        {
            var composited = await CompositeOverlayAsync(slot, publicStore, composer, now, ct);
            if (composited is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "The chosen variant is no longer in the public container.");
            }
            fontFallback = composited.FontFallback;
        }
        else if (!string.IsNullOrWhiteSpace(slot.HeadlineText))
        {
            var composited = await CompositeSlotAsync(slot, slot.HeadlineText!, slot.SafeArea, slot.HeadlineBackground, publicStore, composer, now, ct);
            if (composited is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "The chosen variant is no longer in the public container.");
            }
            fontFallback = composited.FontFallback;
        }

        long? blogVersion = null;
        var artifactToPatch = request.BlogArtifactId ?? slot.ArtifactId;
        if (artifactToPatch is { } blogId)
        {
            var blog = await db.Artifacts.SingleOrDefaultAsync(
                a => a.Id == blogId && a.CampaignId == campaignId, ct);
            if (blog is null)
            {
                return Results.NotFound();
            }
            // Manuscript stubs use the generator's slot vocabulary; try both it and
            // the plan's kind so either marker style is replaced.
            var rendered = new List<RenderedImage>
            {
                new(slot.Kind, slot.PublishedUrl!),
                new(ImagePlanService.MapPromptSlot(slot.Kind), slot.PublishedUrl!),
            };
            if (ImageEndpoints.ReplaceStubs(blog.ContentJson, rendered) is { } updated)
            {
                await ArtifactEndpoints.SnapshotRevisionAsync(db, blog, "image-placed", now, ct);
                blog.ContentJson = updated;
                blog.Version++;
                blog.UpdatedAt = now;
            }
            blogVersion = blog.Version;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { slot = ToResponse(slot), blogVersion, fontFallback });
    }

    private static async Task<IResult> CompositeAsync(
        CompositeHeadlineRequest request,
        IPublicContentStore publicStore,
        IImageComposer composer,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var slot = await LoadSlotAsync(request.CampaignId, request.SlotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        if (slot.BaseImagePath is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "This slot has no placed image to composite onto.");
        }

        var now = clock.GetUtcNow();
        slot.HeadlineText = request.Headline;
        slot.SafeArea = request.SafeArea;
        slot.HeadlineBackground = request.HeadlineBackground;
        var composited = await CompositeSlotAsync(
            slot, request.Headline, request.SafeArea, request.HeadlineBackground, publicStore, composer, now, ct);
        if (composited is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "The slot's base image is no longer in the public container.");
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            slot = ToResponse(slot),
            composited.FontFallback,
            composited.Typeface,
        });
    }

    private static async Task<IResult> ClearAsync(
        Guid campaignId, Guid slotId, CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var slot = await LoadSlotAsync(campaignId, slotId, db, ct);
        if (slot is null)
        {
            return Results.NotFound();
        }
        // Clearing resets state, not the prompt: the prompt is the user's work.
        slot.State = "Empty";
        slot.PublishedUrl = null;
        slot.BaseImagePath = null;
        slot.BaseImageUrl = null;
        slot.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(slot));
    }

    // ---- Internals -------------------------------------------------------------

    private static Task<ImageSlot?> LoadSlotAsync(
        Guid campaignId, Guid slotId, CastmillDbContext db, CancellationToken ct) =>
        db.ImageSlots.SingleOrDefaultAsync(s => s.Id == slotId && s.CampaignId == campaignId, ct);

    private static async Task<CompositeResult?> CompositeSlotAsync(
        ImageSlot slot, string headline, bool safeArea, string? background,
        IPublicContentStore publicStore, IImageComposer composer,
        DateTimeOffset now, CancellationToken ct)
    {
        var baseBytes = await publicStore.ReadAsync(slot.BaseImagePath!, ct);
        if (baseBytes is null)
        {
            return null;
        }
        var result = composer.ComposeHeadline(baseBytes, headline, safeArea, background);
        var url = await publicStore.PublishAsync(
            CompositePath(slot.CampaignId, slot.Kind), result.Image, "image/webp", ct);
        slot.PublishedUrl = url.ToString();
        slot.UpdatedAt = now;
        return result;
    }

    private static string VariantPath(Guid campaignId, string kind, int index) =>
        $"campaigns/{campaignId}/images/{kind}/variants/{index}-{Guid.NewGuid():N}.webp";

    private static string ThumbPath(Guid campaignId, string kind) =>
        $"campaigns/{campaignId}/images/{kind}/variants/thumbs/{Guid.NewGuid():N}.webp";

    private static string CompositePath(Guid campaignId, string kind) =>
        $"campaigns/{campaignId}/images/{kind}/composited/{Guid.NewGuid():N}.webp";

    /// <summary>
    /// Recovers the blob path from a variant URL and verifies it belongs to this
    /// campaign's slot — a client may not point a slot at arbitrary content.
    /// </summary>
    internal static string? PathFromVariantUrl(string url, Guid campaignId, string kind)
    {
        var prefix = $"campaigns/{campaignId}/images/{kind}/variants/";
        var index = url.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }
        var path = url[index..];
        // No query strings, no traversal: the path must be exactly what we published.
        return path.Contains('?', StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)
            ? null
            : path;
    }
}
