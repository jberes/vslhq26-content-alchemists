using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Images;
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
            s.SourceSegmentId, s.HeadlineText, s.SafeArea, s.State, s.PublishedUrl, s.BaseImageUrl, s.UpdatedAt);

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

    private static async Task<IResult> ReserveAsync(
        Guid campaignId, IImagePlanService plan, CastmillDbContext db, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        var slots = await plan.EnsureSlotsAsync(campaignId, ct);
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

    /// <summary>Generates N variants at the slot's exact dimensions and publishes each for review.</summary>
    private static async Task<IResult> GenerateAsync(
        Guid campaignId,
        Guid slotId,
        GenerateVariantsRequest request,
        ClaimsPrincipal principal,
        IImageRenderer renderer,
        IPublicContentStore publicStore,
        CastmillDbContext db,
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

        var userId = AuthEndpoints.GetUserId(principal);
        var variants = new List<ImageVariantResponse>();
        var failures = new List<string>();

        for (var i = 1; i <= request.Variants; i++)
        {
            try
            {
                var webp = await renderer.RenderExactAsync(
                    userId, slot.Prompt!, slot.TargetWidth, slot.TargetHeight, slot.ModelAlias, ct);
                // Unique name: published blobs carry immutable cache headers, so a
                // variant must never reuse a path a previous one held.
                var url = await publicStore.PublishAsync(
                    VariantPath(campaignId, slot.Kind, i), webp, "image/webp", ct);
                variants.Add(new ImageVariantResponse(i, url.ToString(), slot.ModelAlias ?? "image"));
            }
            catch (AiNotConfiguredException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"v{i}: {ex.GetType().Name}"); // partial failure never sinks the set
            }
        }

        return Results.Ok(new { slotId = slot.Id, slot.Kind, variants, failures });
    }

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
        var basePath = PathFromVariantUrl(request.Url, campaignId, slot.Kind);
        if (basePath is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Url"] = ["The URL is not a variant published for this slot."],
            });
        }

        var now = clock.GetUtcNow();
        slot.BaseImagePath = basePath;
        slot.BaseImageUrl = request.Url;
        slot.PublishedUrl = request.Url;
        slot.State = "Filled";
        slot.UpdatedAt = now;

        bool? fontFallback = null;
        if (!string.IsNullOrWhiteSpace(slot.HeadlineText))
        {
            var composited = await CompositeSlotAsync(slot, slot.HeadlineText!, slot.SafeArea, publicStore, composer, now, ct);
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
        var composited = await CompositeSlotAsync(slot, request.Headline, request.SafeArea, publicStore, composer, now, ct);
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
        ImageSlot slot, string headline, bool safeArea,
        IPublicContentStore publicStore, IImageComposer composer,
        DateTimeOffset now, CancellationToken ct)
    {
        var baseBytes = await publicStore.ReadAsync(slot.BaseImagePath!, ct);
        if (baseBytes is null)
        {
            return null;
        }
        var result = composer.ComposeHeadline(baseBytes, headline, safeArea);
        var url = await publicStore.PublishAsync(
            CompositePath(slot.CampaignId, slot.Kind), result.Image, "image/webp", ct);
        slot.PublishedUrl = url.ToString();
        slot.UpdatedAt = now;
        return result;
    }

    private static string VariantPath(Guid campaignId, string kind, int index) =>
        $"campaigns/{campaignId}/images/{kind}/variants/{index}-{Guid.NewGuid():N}.webp";

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
