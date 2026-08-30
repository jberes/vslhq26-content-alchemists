using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Brands;

public sealed record BrandAccess(BrandProfile Brand, bool IsOwner);
public sealed record AssetAccess(Asset Asset, Guid? SharedBrandId, Guid? SharedCampaignId = null);

public interface IBrandAccessService
{
    Task<BrandAccess?> FindAsync(
        Guid brandId,
        Guid userId,
        Guid tenantId,
        bool tracking,
        CancellationToken ct);

    Task<IReadOnlyList<BrandAccess>> ListAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct);

    Task<AssetAccess?> FindAccessibleAssetAsync(
        Guid assetId,
        Guid userId,
        Guid tenantId,
        CancellationToken ct);

    Task<IReadOnlyList<AssetAccess>> ListAccessibleAssetsAsync(
        IReadOnlyCollection<Guid> assetIds,
        Guid userId,
        Guid tenantId,
        CancellationToken ct);
}

public sealed class BrandAccessService(CastmillDbContext db, ITenantProvider tenant) : IBrandAccessService
{
    public async Task<BrandAccess?> FindAsync(
        Guid brandId,
        Guid userId,
        Guid tenantId,
        bool tracking,
        CancellationToken ct)
    {
        var sharedBrandIds = db.BrandCollaborators
            .IgnoreQueryFilters()
            .Where(collaborator => collaborator.UserId == userId)
            .Select(collaborator => collaborator.BrandId);
        var query = db.BrandProfiles.IgnoreQueryFilters()
            .Where(brand => brand.Id == brandId
                && (brand.TenantId == tenantId || sharedBrandIds.Contains(brand.Id)));
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query
            .Select(brand => new BrandAccess(brand, brand.TenantId == tenantId))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<BrandAccess>> ListAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct)
    {
        var sharedBrandIds = db.BrandCollaborators
            .IgnoreQueryFilters()
            .Where(collaborator => collaborator.UserId == userId)
            .Select(collaborator => collaborator.BrandId);
        return await db.BrandProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(brand => brand.TenantId == tenantId || sharedBrandIds.Contains(brand.Id))
            .OrderBy(brand => brand.Name)
            .Select(brand => new BrandAccess(brand, brand.TenantId == tenantId))
            .ToListAsync(ct);
    }

    public async Task<AssetAccess?> FindAccessibleAssetAsync(
        Guid assetId,
        Guid userId,
        Guid tenantId,
        CancellationToken ct)
    {
        return (await ListAccessibleAssetsAsync([assetId], userId, tenantId, ct))
            .SingleOrDefault();
    }

    public async Task<IReadOnlyList<AssetAccess>> ListAccessibleAssetsAsync(
        IReadOnlyCollection<Guid> assetIds,
        Guid userId,
        Guid tenantId,
        CancellationToken ct)
    {
        var owned = await db.Assets.IgnoreQueryFilters().AsNoTracking()
            .Where(asset => assetIds.Contains(asset.Id) && asset.TenantId == tenantId)
            .ToListAsync(ct);
        var ownedIds = owned.Select(asset => asset.Id).ToHashSet();
        var missingIds = assetIds.Where(assetId => !ownedIds.Contains(assetId)).ToList();
        if (missingIds.Count == 0)
        {
            return owned.Select(asset => new AssetAccess(asset, null)).ToList();
        }

        var accessibleCampaignIds = db.Campaigns.IgnoreQueryFilters()
            .Where(campaign => campaign.TenantId == tenantId
                || (tenant.NormalizedEmail != null
                    && ((campaign.ShareDomain != null
                            && campaign.ShareDomain == tenant.EmailDomain)
                        || db.CampaignCollaborators.IgnoreQueryFilters().Any(collaborator =>
                            collaborator.CampaignId == campaign.Id
                            && collaborator.NormalizedEmail == tenant.NormalizedEmail))))
            .Select(campaign => campaign.Id);

        var campaignAssets = await db.MediaUploads.IgnoreQueryFilters().AsNoTracking()
            .Where(upload => missingIds.Contains(upload.AssetId)
                && accessibleCampaignIds.Contains(upload.CampaignId))
            .Join(db.Assets.IgnoreQueryFilters().AsNoTracking(),
                upload => upload.AssetId,
                asset => asset.Id,
                (upload, asset) => new { asset, upload.CampaignId })
            .ToListAsync(ct);
        var campaignAssetIds = campaignAssets.Select(item => item.asset.Id).ToHashSet();
        missingIds = missingIds.Where(assetId => !campaignAssetIds.Contains(assetId)).ToList();

        var campaignBrandAssets = await (
            from link in db.BrandAssets.IgnoreQueryFilters().AsNoTracking()
            where missingIds.Contains(link.AssetId)
            from campaign in db.Campaigns.IgnoreQueryFilters()
                .Where(campaign => campaign.BrandId == link.BrandId
                    && accessibleCampaignIds.Contains(campaign.Id))
            join asset in db.Assets.IgnoreQueryFilters().AsNoTracking()
                on link.AssetId equals asset.Id
            select new { asset, campaign.Id })
            .ToListAsync(ct);
        var campaignBrandAssetIds = campaignBrandAssets.Select(item => item.asset.Id).ToHashSet();
        missingIds = missingIds.Where(assetId => !campaignBrandAssetIds.Contains(assetId)).ToList();

        var shared = await db.BrandAssets.IgnoreQueryFilters().AsNoTracking()
            .Where(link => missingIds.Contains(link.AssetId)
                && (db.BrandProfiles.IgnoreQueryFilters().Any(brand =>
                        brand.Id == link.BrandId && brand.TenantId == tenantId)
                    || db.BrandCollaborators.IgnoreQueryFilters().Any(collaborator =>
                        collaborator.BrandId == link.BrandId && collaborator.UserId == userId)))
            .Join(db.Assets.IgnoreQueryFilters().AsNoTracking(),
                link => link.AssetId,
                asset => asset.Id,
                (link, asset) => new { asset, link.BrandId })
            .OrderBy(item => item.BrandId)
            .ToListAsync(ct);

        return owned.Select(asset => new AssetAccess(asset, null))
            .Concat(campaignAssets
                .DistinctBy(item => item.asset.Id)
                .Select(item => new AssetAccess(item.asset, null, item.CampaignId)))
            .Concat(campaignBrandAssets
                .DistinctBy(item => item.asset.Id)
                .Select(item => new AssetAccess(item.asset, null, item.Id)))
            .Concat(shared
                .DistinctBy(item => item.asset.Id)
                .Select(item => new AssetAccess(item.asset, item.BrandId)))
            .ToList();
    }
}