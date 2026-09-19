using System.Security.Claims;
using System.Globalization;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Brands;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public static class VideoReferenceEndpoints
{
    private static readonly string[] Goals = ["product", "ui-demonstration", "people", "key-moments"];
    private static readonly string[] CropMethods = ["none", "automatic", "manual"];
    private static readonly string[] SelectionMethods = ["automatic", "ai-ranked", "manual"];

    public static IEndpointRouteBuilder MapVideoReferenceEndpoints(this IEndpointRouteBuilder routes)
    {
        var campaign = routes.MapGroup("/api/v1/campaigns/{campaignId:guid}")
            .RequireAuthorization("TenantAllowed");
        campaign.MapGet("/reference-sets", ListAsync);
        campaign.MapPost("/reference-sets", CreateAsync)
            .Validate<VideoReferenceSetCreateRequest>().RequireRateLimiting("writes");
        campaign.MapPut("/reference-images/{imageId:guid}", UpdateImageAsync)
            .Validate<VideoReferenceImageUpdateRequest>().RequireRateLimiting("writes");
        campaign.MapPost("/reference-images/{imageId:guid}/restore-full", RestoreFullAsync)
            .RequireRateLimiting("writes");
        campaign.MapPost("/reference-analysis", AnalyzeAsync)
            .Validate<VideoReferenceAiRequest>().RequireRateLimiting("ai");

        var brand = routes.MapGroup("/api/v1/brands/{brandId:guid}/reference-crop-presets")
            .RequireAuthorization("TenantAllowed");
        brand.MapGet("/", ListPresetsAsync);
        brand.MapPost("/", SavePresetAsync)
            .Validate<ReferenceCropPresetRequest>().RequireRateLimiting("writes");
        return routes;
    }

    private static async Task<IResult> ListAsync(
        Guid campaignId, Guid? videoAssetId, CastmillDbContext db, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(item => item.Id == campaignId, ct)) return Results.NotFound();
        var query = db.ReferenceSets.Where(item => item.CampaignId == campaignId);
        if (videoAssetId is { } sourceId) query = query.Where(item => item.VideoAssetId == sourceId);
        var sets = await query.OrderByDescending(item => item.CreatedAt).ToListAsync(ct);
        var ids = sets.Select(item => item.Id).ToList();
        var images = await db.ReferenceImages.Where(item => ids.Contains(item.ReferenceSetId))
            .OrderBy(item => item.SortOrder).ToListAsync(ct);
        return Results.Ok(sets.Select(set => ToResponse(set,
            images.Where(image => image.ReferenceSetId == set.Id).ToList())).ToList());
    }

    private static async Task<IResult> CreateAsync(
        Guid campaignId,
        VideoReferenceSetCreateRequest request,
        ClaimsPrincipal principal,
        ITenantProvider tenant,
        IBrandAccessService brandAccess,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(item => item.Id == campaignId, ct);
        if (campaign is null) return Results.NotFound();
        if (campaign.BrandId != request.BrandId)
            return Results.Problem("The reference set must use this campaign's brand.", statusCode: 400);
        var brand = await brandAccess.FindAsync(request.BrandId, AuthEndpoints.GetUserId(principal),
            tenant.TenantId!.Value, tracking: false, ct);
        if (brand is null) return Results.NotFound();
        if (!Goals.Contains(request.SelectionGoal, StringComparer.Ordinal))
            return Results.Problem($"Selection goal must be one of: {string.Join(", ", Goals)}.", statusCode: 400);
        var source = await db.SourceAssets.SingleOrDefaultAsync(
            item => item.Id == request.VideoAssetId && item.CampaignId == campaignId, ct);
        if (source is null || !source.Modality.Equals(SourceModalities.Media, StringComparison.Ordinal))
            return Results.Problem("Choose a video source from this campaign.", statusCode: 400);
        var validation = ValidateImages(request.Images, request.SourceWidth, request.SourceHeight);
        if (validation is not null) return validation;

        var assetIds = request.Images.SelectMany(item => new[] { item.SourceFrameAssetId, item.DerivedAssetId })
            .Distinct().ToList();
        var assets = await db.Assets.Where(item => assetIds.Contains(item.Id)).ToListAsync(ct);
        if (assets.Count != assetIds.Count || assets.Any(item => !item.ContentType.StartsWith("image/", StringComparison.Ordinal)))
            return Results.Problem("Every saved frame must reference uploaded image assets owned by the current tenant.", statusCode: 400);

        var now = clock.GetUtcNow();
        var set = new ReferenceSet
        {
            Id = Guid.NewGuid(), TenantId = campaign.TenantId, BrandId = request.BrandId,
            CampaignId = campaignId, VideoAssetId = source.Id, Name = request.Name.Trim(),
            Purpose = request.Purpose?.Trim(), SelectionGoal = request.SelectionGoal,
            DurationMs = request.DurationMs, SourceWidth = request.SourceWidth,
            SourceHeight = request.SourceHeight, FrameRate = request.FrameRate,
            CreatedBy = AuthEndpoints.GetUserId(principal), CreatedAt = now,
        };
        var images = request.Images.Select(item => new ReferenceImage
        {
            Id = Guid.NewGuid(), TenantId = campaign.TenantId, ReferenceSetId = set.Id,
            BrandId = request.BrandId, CampaignId = campaignId, VideoAssetId = source.Id,
            SourceFrameAssetId = item.SourceFrameAssetId, DerivedAssetId = item.DerivedAssetId,
            SourceTimestampMs = item.SourceTimestampMs, SourceFrameNumber = item.SourceFrameNumber,
            CropX = item.Crop.X, CropY = item.Crop.Y, CropWidth = item.Crop.Width,
            CropHeight = item.Crop.Height, CropMethod = item.Crop.Method,
            SelectionMethod = item.SelectionMethod, PerceptualHash = item.PerceptualHash,
            QualityScore = item.QualityScore, AiSummary = item.AiSummary,
            Label = string.IsNullOrWhiteSpace(item.Label) ? $"Frame {FormatTimestamp(item.SourceTimestampMs)}" : item.Label.Trim(),
            SortOrder = item.SortOrder, CreatedAt = now, UpdatedAt = now,
        }).ToList();

        db.ReferenceSets.Add(set);
        db.ReferenceImages.AddRange(images);
        foreach (var image in images)
        {
            if (!await db.BrandAssets.IgnoreQueryFilters().AnyAsync(
                    link => link.BrandId == request.BrandId && link.AssetId == image.DerivedAssetId, ct))
            {
                db.BrandAssets.Add(new BrandAsset
                {
                    Id = Guid.NewGuid(), TenantId = brand.Brand.TenantId, BrandId = request.BrandId,
                    AssetId = image.DerivedAssetId, Kind = "product", Label = image.Label, CreatedAt = now,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/campaigns/{campaignId}/reference-sets/{set.Id}", ToResponse(set, images));
    }

    private static IResult? ValidateImages(
        IReadOnlyList<VideoReferenceImageCreateRequest> images, int sourceWidth, int sourceHeight)
    {
        if (images.Count is < 1 or > 30)
            return Results.Problem("Save between 1 and 30 reference images.", statusCode: 400);
        foreach (var image in images)
        {
            if (!SelectionMethods.Contains(image.SelectionMethod, StringComparer.Ordinal)
                || !CropMethods.Contains(image.Crop.Method, StringComparer.Ordinal))
                return Results.Problem("A frame has an unknown selection or crop method.", statusCode: 400);
            if (image.Crop.X + image.Crop.Width > sourceWidth
                || image.Crop.Y + image.Crop.Height > sourceHeight)
                return Results.Problem("A crop extends outside the source video frame.", statusCode: 400);
        }
        var duplicate = images.GroupBy(item => item.SourceTimestampMs)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null
            : Results.Problem("The same video moment cannot be saved twice in one set.", statusCode: 400);
    }

    private static async Task<IResult> UpdateImageAsync(
        Guid campaignId, Guid imageId, VideoReferenceImageUpdateRequest request,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var image = await db.ReferenceImages.SingleOrDefaultAsync(
            item => item.Id == imageId && item.CampaignId == campaignId, ct);
        if (image is null) return Results.NotFound();
        var set = await db.ReferenceSets.SingleAsync(item => item.Id == image.ReferenceSetId, ct);
        if (!CropMethods.Contains(request.Crop.Method, StringComparer.Ordinal)
            || request.Crop.X + request.Crop.Width > set.SourceWidth
            || request.Crop.Y + request.Crop.Height > set.SourceHeight)
            return Results.Problem("The crop extends outside the source frame.", statusCode: 400);
        if (!await db.Assets.AnyAsync(item => item.Id == request.DerivedAssetId
                && item.ContentType.StartsWith("image/"), ct)) return Results.NotFound();
        await SwapBrandAssetAsync(image, request.DerivedAssetId, db, clock.GetUtcNow(), ct);
        image.DerivedAssetId = request.DerivedAssetId;
        image.CropX = request.Crop.X; image.CropY = request.Crop.Y;
        image.CropWidth = request.Crop.Width; image.CropHeight = request.Crop.Height;
        image.CropMethod = request.Crop.Method;
        if (!string.IsNullOrWhiteSpace(request.Label)) image.Label = request.Label.Trim();
        image.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(image));
    }

    private static async Task<IResult> RestoreFullAsync(
        Guid campaignId, Guid imageId, CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var image = await db.ReferenceImages.SingleOrDefaultAsync(
            item => item.Id == imageId && item.CampaignId == campaignId, ct);
        if (image is null) return Results.NotFound();
        var set = await db.ReferenceSets.SingleAsync(item => item.Id == image.ReferenceSetId, ct);
        await SwapBrandAssetAsync(image, image.SourceFrameAssetId, db, clock.GetUtcNow(), ct);
        image.DerivedAssetId = image.SourceFrameAssetId;
        image.CropX = 0; image.CropY = 0; image.CropWidth = set.SourceWidth;
        image.CropHeight = set.SourceHeight; image.CropMethod = "none";
        image.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(image));
    }

    private static async Task SwapBrandAssetAsync(
        ReferenceImage image, Guid newAssetId, CastmillDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (image.DerivedAssetId == newAssetId) return;
        var targetExists = await db.BrandAssets.IgnoreQueryFilters().AnyAsync(
            link => link.BrandId == image.BrandId && link.AssetId == newAssetId, ct);
        // Never unlink the previous derivative here: another saved set or placed image may
        // already depend on that Brand-library entry. Revision adds the replacement to the
        // library and switches only this reference image's active pointer.
        if (!targetExists)
        {
            db.BrandAssets.Add(new BrandAsset
            {
                Id = Guid.NewGuid(), TenantId = image.TenantId, BrandId = image.BrandId,
                AssetId = newAssetId, Kind = "product", Label = image.Label, CreatedAt = now,
            });
        }
    }

    private static async Task<IResult> AnalyzeAsync(
        Guid campaignId, VideoReferenceAiRequest request, ClaimsPrincipal principal,
        CastmillDbContext db, IVideoReferenceAiService service, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(item => item.Id == campaignId, ct)) return Results.NotFound();
        if (!Goals.Contains(request.Goal, StringComparer.Ordinal))
            return Results.Problem($"Goal must be one of: {string.Join(", ", Goals)}.", statusCode: 400);
        return Results.Ok(await service.AnalyzeAsync(AuthEndpoints.GetUserId(principal), request, ct));
    }

    private static async Task<IResult> ListPresetsAsync(
        Guid brandId, ClaimsPrincipal principal, ITenantProvider tenant, IBrandAccessService access,
        CastmillDbContext db, CancellationToken ct)
    {
        var grant = await access.FindAsync(brandId, AuthEndpoints.GetUserId(principal),
            tenant.TenantId!.Value, false, ct);
        if (grant is null) return Results.NotFound();
        var presets = await db.ReferenceCropPresets.IgnoreQueryFilters()
            .Where(item => item.BrandId == brandId && item.TenantId == grant.Brand.TenantId)
            .OrderBy(item => item.Name).ToListAsync(ct);
        return Results.Ok(presets.Select(ToResponse).ToList());
    }

    private static async Task<IResult> SavePresetAsync(
        Guid brandId, ReferenceCropPresetRequest request, ClaimsPrincipal principal,
        ITenantProvider tenant, IBrandAccessService access, CastmillDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var grant = await access.FindAsync(brandId, userId, tenant.TenantId!.Value, false, ct);
        if (grant is null) return Results.NotFound();
        if (request.Crop.X + request.Crop.Width > request.SourceWidth
            || request.Crop.Y + request.Crop.Height > request.SourceHeight)
            return Results.Problem("The crop extends outside the preset source size.", statusCode: 400);
        var now = clock.GetUtcNow();
        var preset = await db.ReferenceCropPresets.IgnoreQueryFilters().SingleOrDefaultAsync(
            item => item.BrandId == brandId && item.TenantId == grant.Brand.TenantId
                && item.Name == request.Name.Trim(), ct);
        if (preset is null)
        {
            preset = new ReferenceCropPreset
            {
                Id = Guid.NewGuid(), TenantId = grant.Brand.TenantId, BrandId = brandId,
                Name = request.Name.Trim(), CreatedBy = userId, CreatedAt = now,
                Kind = request.Kind,
            };
            db.ReferenceCropPresets.Add(preset);
        }
        preset.SourceWidth = request.SourceWidth; preset.SourceHeight = request.SourceHeight;
        preset.CropX = request.Crop.X; preset.CropY = request.Crop.Y;
        preset.CropWidth = request.Crop.Width; preset.CropHeight = request.Crop.Height;
        preset.Kind = request.Kind; preset.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(preset));
    }

    private static ReferenceCropPresetResponse ToResponse(ReferenceCropPreset item) =>
        new(item.Id, item.BrandId, item.Name, item.SourceWidth, item.SourceHeight,
            new VideoReferenceCropDto(item.CropX, item.CropY, item.CropWidth, item.CropHeight, "manual"),
            item.Kind, item.UpdatedAt);

    private static VideoReferenceSetResponse ToResponse(ReferenceSet set, IReadOnlyList<ReferenceImage> images) =>
        new(set.Id, set.BrandId, set.CampaignId, set.VideoAssetId, set.Name, set.Purpose,
            set.SelectionGoal, set.DurationMs, set.SourceWidth, set.SourceHeight, set.FrameRate,
            set.CreatedAt, images.Select(ToResponse).ToList());

    private static VideoReferenceImageResponse ToResponse(ReferenceImage image) =>
        new(image.Id, image.ReferenceSetId, image.VideoAssetId, image.SourceFrameAssetId,
            image.DerivedAssetId, image.SourceTimestampMs, image.SourceFrameNumber,
            new VideoReferenceCropDto(image.CropX, image.CropY, image.CropWidth,
                image.CropHeight, image.CropMethod), image.SelectionMethod, image.PerceptualHash,
            image.QualityScore, image.AiSummary, image.Label, image.SortOrder,
            image.CreatedAt, image.UpdatedAt);

    private static string FormatTimestamp(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
