using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
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
        group.MapPatch("/{slotId:guid}", PatchAsync).Validate<ImageSlotPatchRequest>().RequireRateLimiting("writes");
        group.MapPost("/{slotId:guid}/generate", GenerateAsync).Validate<GenerateVariantsRequest>().RequireRateLimiting("ai");
        group.MapPost("/{slotId:guid}/place", PlaceAsync).Validate<PlaceVariantRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{slotId:guid}", ClearAsync).RequireRateLimiting("writes");
        group.MapDelete("/{slotId:guid}/variants/{variantId:guid}", DeleteVariantAsync)
            .RequireRateLimiting("writes");

        // Persisted takes: the gallery lists them, keep/discard flips state, steer
        // makes a new take from an old one. Blobs are never deleted (immutable cache).
        group.MapGet("/{slotId:guid}/variants", ListVariantsAsync);
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
            [.. ImageReferenceResolver.ParseIds(s.ReferenceAssetIdsJson)]);

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
            var valid = brandId is { } id
                ? await db.BrandAssets.CountAsync(
                    a => a.BrandId == id && requestedReferences.Contains(a.Id), ct)
                : 0;
            if (valid != requestedReferences.Distinct().Count())
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ReferenceAssetIds"] = ["Every reference must belong to this campaign's brand kit."],
                });
            }
        }
        slot.Prompt = request.Prompt ?? slot.Prompt;
        slot.ModelAlias = request.ModelAlias ?? slot.ModelAlias;
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
        var basePrompt = slot.PromptMode == "Manual"
            ? slot.Prompt!
            : BuildAutoPrompt(slot, campaign, owner);
        var effectivePrompt = ComposeEffectivePrompt(
            basePrompt, slot.PromptMode == "Manual" ? null : brand.ImageStyleBlock, steeringNote: null,
            slot.PromptMode == "Manual"
                ? null
                : CampaignEndpoints.ParseSeoTargets(campaign.SeoTargetsJson).PrimaryKeyword);
        effectivePrompt = AppendReferenceInstructions(effectivePrompt, resolvedReferences);
        effectivePrompt = AppendSlotCompositionGuardrails(effectivePrompt, slot);

        return await RenderBatchAsync(
            slot, effectivePrompt, request.Variants, steeringNote: null, sourceVariantId: null,
            principal, http, renderer, publicStore, composer, tenant, db, clock, ct,
            resolvedReferences, request.ModelAlias);
    }

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
        var variant = await db.ImageVariants.SingleOrDefaultAsync(
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

        db.ImageVariants.Remove(variant);
        await db.SaveChangesAsync(ct);

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

    private static async Task<IResult> ListVariantsAsync(
        Guid campaignId, Guid slotId, CastmillDbContext db, CancellationToken ct,
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
        return Results.Ok(variants.Select(ToResponse).ToList());
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
        var effectivePrompt = ComposeEffectivePrompt(source.Prompt, imageStyleBlock: null, note);
        var campaign = await db.Campaigns.SingleAsync(c => c.Id == campaignId, ct);
        var resolvedReferences = await references.ResolveAsync(campaign, slot, ct);
        effectivePrompt = AppendReferenceInstructions(effectivePrompt, resolvedReferences);
        effectivePrompt = AppendSlotCompositionGuardrails(effectivePrompt, slot);

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
        string? modelOverride = null)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var now = clock.GetUtcNow();
        // The batch's model: the caller's choice for this run, else the slot's saved default.
        // Recorded on every variant, so a gallery of takes from two models stays readable.
        var model = string.IsNullOrWhiteSpace(modelOverride) ? slot.ModelAlias : modelOverride.Trim();

        var run = new GenerationRun
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
        http.Response.Headers.Append("Castmill-Run-Id", run.Id.ToString());

        var variants = new List<ImageVariantResponse>();
        var failures = new List<string>();
        var items = new List<object>();

        for (var i = 1; i <= count; i++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var webp = await renderer.RenderExactAsync(
                    userId, effectivePrompt, slot.TargetWidth, slot.TargetHeight, model,
                    references ?? [], ct);
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
                    Prompt = effectivePrompt,
                    SteeringNote = steeringNote,
                    SourceVariantId = sourceVariantId,
                    State = "Candidate",
                    Width = slot.TargetWidth,
                    Height = slot.TargetHeight,
                    CreatedAt = clock.GetUtcNow(),
                };
                db.ImageVariants.Add(variant);
                variants.Add(ToResponse(variant));
                items.Add(new { kind = $"v{i}", success = true, durationMs = stopwatch.ElapsedMilliseconds });
            }
            catch (AiNotConfiguredException ex)
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
                failures.Add($"v{i}: {reason}"); // partial failure never sinks the set
                items.Add(new { kind = $"v{i}", success = false, error = reason, durationMs = stopwatch.ElapsedMilliseconds });
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

    /// <summary>
    /// Typography safety, appended to EVERY image prompt.
    ///
    /// This is not stylistic advice — it compensates for something the pipeline actually does.
    /// Image models emit their own fixed sizes (1024x1024, 1024x1536, 1536x1024), and the
    /// renderer then crops that output to the slot's exact dimensions, which are a different
    /// aspect ratio. Anything the model placed near an edge is inside the strip that gets cut,
    /// which is why generated text kept arriving clipped along the top or the left.
    ///
    /// The margin is expressed as a fraction rather than pixels because the model does not
    /// know the slot size and never sees the crop.
    /// </summary>
    internal const string TypographyGuardrails = """
        Text rendering rules (follow exactly):
        - Keep every word, letter, logo and supporting graphic inside the middle 76% of the
          frame, leaving at least 12% clear on every edge. Nothing meaningful may touch or
          run off an edge.
        - Use only text explicitly requested by the prompt or visible in an authoritative
          product reference. Do not invent extra marketing captions, statistics, feature
          tiles, footers, badges or interface labels.
        - Leave generous empty margin around any text. Keep a text block to at most three
          short lines and no more than 55% of the image height.
        - Render each word complete and correctly spelled. No cut-off glyphs, no clipped
          descenders, no words continuing past the border.
        - Use few words at a large size rather than many words small; if the text will not fit
          comfortably inside the safe area, omit secondary copy instead of shrinking or
          clipping it.
        - Keep text on an area of flat, contrasting tone so it stays legible.
        """;

    /// <summary>slot prompt/base prompt + brand image style + campaign keyword + the user's adjustment.</summary>
    internal static string ComposeEffectivePrompt(
        string basePrompt, string? imageStyleBlock, string? steeringNote, string? primaryKeyword = null)
    {
        var prompt = basePrompt.Trim();
        if (!string.IsNullOrWhiteSpace(imageStyleBlock))
        {
            prompt = $"{prompt}\n{imageStyleBlock}";
        }

        // The campaign's primary keyword steers any TEXT the image carries (thumbnail
        // headlines especially): a thumbnail that says what people search for is the SEO
        // surface YouTube actually shows. Phrasing only — the model must not paint keyword
        // lists into scenery.
        if (!string.IsNullOrWhiteSpace(primaryKeyword))
        {
            prompt = $"{prompt}\nIf the image contains any text, prefer wording that uses "
                + $"\"{primaryKeyword.Trim()}\" naturally. Never render a list of keywords.";
        }

        if (!string.IsNullOrWhiteSpace(steeringNote))
        {
            prompt = $"{prompt}\nAdjustment: {steeringNote.Trim()}";
        }

        return prompt;
    }

    /// <summary>
    /// Model providers render only a few native canvas sizes, then Castmill crops/resizes to
    /// the durable slot. Giving the model the final dimensions and ratio makes it compose for
    /// that destination; repeating the safe-zone rules last prevents brand/reference text
    /// from pushing essential content into the crop.
    /// </summary>
    internal static string AppendSlotCompositionGuardrails(string prompt, ImageSlot slot)
    {
        var divisor = GreatestCommonDivisor(slot.TargetWidth, slot.TargetHeight);
        var ratioWidth = slot.TargetWidth / divisor;
        var ratioHeight = slot.TargetHeight / divisor;
        return $$"""
            {{prompt.Trim()}}
            Final composition target (follow exactly):
            - The published image is {{slot.TargetWidth}}×{{slot.TargetHeight}} pixels,
              aspect ratio {{ratioWidth}}:{{ratioHeight}}. Compose for this landscape/portrait
              ratio now; do not design an edge-to-edge layout that only works on a square canvas.
            - Center the complete composition and keep every essential subject, product panel,
              label and decorative element inside the middle 76% of the frame so the final
              aspect-ratio crop cannot remove it.
            - The frame must read as one finished composition at the target size. No partial
              cards, clipped rows, cut-off panels or content continuing below the canvas.
            {{TypographyGuardrails}}
            """;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return Math.Max(1, Math.Abs(left));
    }

    internal static string BuildAutoPrompt(ImageSlot slot, Campaign campaign, Artifact? artifact)
    {
        var content = artifact?.ContentJson;
        if (content is { Length: > 5000 })
        {
            content = content[..5000];
        }
        return $$"""
            Create a {{ArtifactDisplayName(slot.Kind)}} for the content item
            "{{artifact?.Title ?? campaign.Name}}".
            Rebuild the composition from the current source every time; do not preserve a
            person, background or product that is no longer present in the references.
            Campaign brief: {{campaign.Brief ?? "(none)"}}
            Content item: {{content ?? "(no structured content)"}}
            {{(string.IsNullOrWhiteSpace(slot.Prompt) ? "" : $"Creative direction: {slot.Prompt}")}}
            """;
    }

    private static string ArtifactDisplayName(string kind) => kind switch
    {
        "youtube-thumbnail" => "YouTube thumbnail",
        "blog-header" => "blog header image",
        _ when kind.StartsWith("blog-inline-", StringComparison.Ordinal) => "supporting blog figure",
        "social-card" => "social-media image",
        _ => "supporting image",
    };

    internal static string AppendReferenceInstructions(
        string prompt, IReadOnlyList<ImageReference> references)
    {
        if (references.Count == 0)
        {
            return prompt;
        }
        var hasProduct = references.Any(r => r.Kind == "product");
        return prompt + "\nActual reference images are attached. Use their pixels, not just "
            + "their descriptions; preserve recognizable faces, objects and layouts."
            + (hasProduct
                ? " Product screenshots are authoritative: reproduce the real interface, "
                    + "including its layout and controls, and never invent replacement UI."
                : string.Empty);
    }

    internal static ImageVariantResponse ToResponse(ImageVariant v) =>
        new(v.Id, v.SlotId, v.Url, v.ThumbUrl, v.Model, v.State,
            v.SteeringNote, v.SourceVariantId, v.Width, v.Height, v.CreatedAt);

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
        if (!string.IsNullOrWhiteSpace(slot.HeadlineText))
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
