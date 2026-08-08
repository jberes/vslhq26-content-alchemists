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
        group.MapPost("/reserve", ReserveAsync).RequireRateLimiting("writes");
        group.MapPatch("/{slotId:guid}", PatchAsync).Validate<ImageSlotPatchRequest>().RequireRateLimiting("writes");
        group.MapPost("/{slotId:guid}/generate", GenerateAsync).Validate<GenerateVariantsRequest>().RequireRateLimiting("ai");
        group.MapPost("/{slotId:guid}/place", PlaceAsync).Validate<PlaceVariantRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{slotId:guid}", ClearAsync).RequireRateLimiting("writes");

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
            s.HeadlineBackground, s.ArtifactId);

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
        slot.Prompt = request.Prompt ?? slot.Prompt;
        slot.ModelAlias = request.ModelAlias ?? slot.ModelAlias;
        slot.SourceSegmentId = request.SourceSegmentId ?? slot.SourceSegmentId;
        slot.HeadlineText = request.HeadlineText ?? slot.HeadlineText;
        slot.SafeArea = request.SafeArea ?? slot.SafeArea;
        slot.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(slot));
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
        if (string.IsNullOrWhiteSpace(slot.Prompt))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Prompt"] = ["This slot has no prompt yet. PATCH one, or seed it from an image-prompts artifact."],
            });
        }

        var campaign = await db.Campaigns.SingleAsync(c => c.Id == campaignId, ct);
        var brand = await brands.ResolveAsync(campaign, ct);
        var effectivePrompt = ComposeEffectivePrompt(slot.Prompt!, brand.ImageStyleBlock, steeringNote: null);

        return await RenderBatchAsync(
            slot, effectivePrompt, request.Variants, steeringNote: null, sourceVariantId: null,
            principal, http, renderer, publicStore, composer, tenant, db, clock, ct);
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
        // the adjustment, so lineage is honest and reproducible.
        var effectivePrompt = ComposeEffectivePrompt(source.Prompt, imageStyleBlock: null, request.Note);

        return await RenderBatchAsync(
            slot, effectivePrompt, request.Variants, request.Note, source.Id,
            principal, http, renderer, publicStore, composer, tenant, db, clock, ct);
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
        CancellationToken ct)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var now = clock.GetUtcNow();

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
                    userId, effectivePrompt, slot.TargetWidth, slot.TargetHeight, slot.ModelAlias, ct);
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
                    Model = slot.ModelAlias ?? "image",
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"v{i}: {ex.GetType().Name}"); // partial failure never sinks the set
                items.Add(new { kind = $"v{i}", success = false, error = ex.GetType().Name, durationMs = stopwatch.ElapsedMilliseconds });
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
        - This image will be CROPPED to a different aspect ratio after generation. Keep every
          word, letter and logo inside the middle 80% of the frame, well clear of all four
          edges. Nothing readable may touch or run off an edge.
        - Leave generous empty margin around any text. Do not fill the frame edge to edge with
          type.
        - Render each word complete and correctly spelled. No cut-off glyphs, no clipped
          descenders, no words continuing past the border.
        - Use few words at a large size rather than many words small; if the text will not fit
          comfortably inside the safe area, use fewer words.
        - Keep text on an area of flat, contrasting tone so it stays legible.
        """;

    /// <summary>slot prompt/base prompt + brand image style + the user's adjustment.</summary>
    internal static string ComposeEffectivePrompt(string basePrompt, string? imageStyleBlock, string? steeringNote)
    {
        var prompt = basePrompt.Trim();
        if (!string.IsNullOrWhiteSpace(imageStyleBlock))
        {
            prompt = $"{prompt}\n{imageStyleBlock}";
        }
        if (!string.IsNullOrWhiteSpace(steeringNote))
        {
            prompt = $"{prompt}\nAdjustment: {steeringNote.Trim()}";
        }

        // Last, so it is the most recent instruction the model reads and a brand style block
        // or an adjustment cannot silently override it.
        return $"{prompt}\n{TypographyGuardrails}";
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
        if (request.BlogArtifactId is { } blogId)
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
