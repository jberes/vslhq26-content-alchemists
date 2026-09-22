using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Data;
using Castmill.Core;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

/// <summary>
/// Deleting a campaign takes its whole trail with it.
///
/// Children have no FK cascade (typed-JSON rows, ADR-003), so every table is deleted by hand
/// and a table that is simply forgotten leaves rows behind silently — nothing fails, the
/// campaign disappears from the list, and the orphans keep a private blob alive with nothing
/// pointing at it. `MediaUpload`, `ReferenceSet`, `ReferenceImage` and `CampaignCollaborator`
/// were all missing until 2026-09-21, which meant an uploaded 72 MB video survived the delete
/// of the campaign it belonged to.
///
/// This test seeds one row in EVERY campaign-scoped table and asserts the lot is gone, so a
/// new entity that misses the delete fails here rather than in someone's storage bill.
/// </summary>
[Collection("api")]
public sealed class CampaignStrongDeleteTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Deleting_a_campaign_removes_every_campaign_scoped_row()
    {
        var (client, userId, tenantId) = await AuthedClientAsync();
        var campaign = await CreateCampaignAsync(client);
        var survivor = await CreateCampaignAsync(client);

        var (assetId, sharedAssetId) = await SeedTrailAsync(campaign.Id, survivor.Id, tenantId, userId);

        var delete = await client.DeleteAsync($"/api/v1/campaigns/{campaign.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await using var db = NewDb();
        Assert.Empty(await db.Artifacts.IgnoreQueryFilters().Where(a => a.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.ImageSlots.IgnoreQueryFilters().Where(s => s.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.ImageVariants.IgnoreQueryFilters().Where(v => v.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.ScheduleEntries.IgnoreQueryFilters().Where(s => s.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.GenerationRuns.IgnoreQueryFilters().Where(r => r.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        // The four that were previously missed:
        Assert.Empty(await db.MediaUploads.IgnoreQueryFilters().Where(u => u.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.ReferenceSets.IgnoreQueryFilters().Where(s => s.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.ReferenceImages.IgnoreQueryFilters().Where(r => r.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.CampaignCollaborators.IgnoreQueryFilters().Where(c => c.CampaignId == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Campaigns.IgnoreQueryFilters().Where(c => c.Id == campaign.Id).ToListAsync(TestContext.Current.CancellationToken));

        // The asset only this campaign used is gone…
        Assert.Null(await db.Assets.IgnoreQueryFilters().SingleOrDefaultAsync(a => a.Id == assetId, TestContext.Current.CancellationToken));
        // …and one still referenced by a surviving campaign is NOT, or deleting a campaign
        // would break an unrelated one that reused the same source video.
        Assert.NotNull(await db.Assets.IgnoreQueryFilters().SingleOrDefaultAsync(a => a.Id == sharedAssetId, TestContext.Current.CancellationToken));
    }

    /// <summary>The surviving campaign keeps everything of its own.</summary>
    [Fact]
    public async Task Deleting_one_campaign_leaves_another_campaigns_content_intact()
    {
        var (client, userId, tenantId) = await AuthedClientAsync();
        var doomed = await CreateCampaignAsync(client);
        var survivor = await CreateCampaignAsync(client);
        await SeedTrailAsync(doomed.Id, survivor.Id, tenantId, userId);

        var artifact = await client.PostAsJsonAsync($"/api/v1/campaigns/{survivor.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Survivor post", "{}"));
        artifact.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/campaigns/{doomed.Id}")).StatusCode);

        await using var db = NewDb();
        Assert.NotEmpty(await db.Artifacts.IgnoreQueryFilters()
            .Where(a => a.CampaignId == survivor.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await db.ReferenceSets.IgnoreQueryFilters()
            .Where(s => s.CampaignId == survivor.Id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_a_campaign_that_is_not_yours_is_a_404()
    {
        var (mine, _, _) = await AuthedClientAsync();
        var campaign = await CreateCampaignAsync(mine);

        var (theirs, _, _) = await AuthedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await theirs.DeleteAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);
    }

    /// <summary>
    /// One row in every campaign-scoped table, plus two assets: one used only by the doomed
    /// campaign, one shared with a campaign that survives.
    /// </summary>
    private async Task<(Guid OwnAsset, Guid SharedAsset)> SeedTrailAsync(
        Guid campaignId, Guid survivorId, Guid tenantId, Guid userId)
    {
        await using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var ownAsset = Guid.NewGuid();
        var sharedAsset = Guid.NewGuid();

        db.Assets.AddRange(
            new Asset { Id = ownAsset, TenantId = tenantId, FileName = "talk.mp4", ContentType = "video/mp4", SizeBytes = 72_600_000, BlobPath = $"private/{ownAsset}.mp4", CreatedAt = now },
            new Asset { Id = sharedAsset, TenantId = tenantId, FileName = "shared.mp4", ContentType = "video/mp4", SizeBytes = 1024, BlobPath = $"private/{sharedAsset}.mp4", CreatedAt = now });

        db.MediaUploads.Add(new MediaUpload
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CampaignId = campaignId, AssetId = ownAsset,
            UploadedBytes = 72_600_000, NextBlockIndex = 3, BlockIdsJson = "[]",
            Status = MediaUploadStatus.Completed, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddDays(1),
        });

        var doomedSet = Guid.NewGuid();
        db.ReferenceSets.Add(new ReferenceSet
        {
            Id = doomedSet, TenantId = tenantId, BrandId = Guid.NewGuid(), CampaignId = campaignId,
            VideoAssetId = ownAsset, Name = "Frames", SelectionGoal = "faces",
            DurationMs = 60_000, SourceWidth = 1920, SourceHeight = 1080, FrameRate = 30,
            CreatedBy = userId, CreatedAt = now,
        });
        // The survivor's set points at the SHARED asset, which must therefore outlive the delete.
        db.ReferenceSets.Add(new ReferenceSet
        {
            Id = Guid.NewGuid(), TenantId = tenantId, BrandId = Guid.NewGuid(), CampaignId = survivorId,
            VideoAssetId = sharedAsset, Name = "Kept", SelectionGoal = "faces",
            DurationMs = 60_000, SourceWidth = 1920, SourceHeight = 1080, FrameRate = 30,
            CreatedBy = userId, CreatedAt = now,
        });
        db.ReferenceImages.Add(new ReferenceImage
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ReferenceSetId = doomedSet, BrandId = Guid.NewGuid(),
            CampaignId = campaignId, VideoAssetId = ownAsset, SourceFrameAssetId = ownAsset,
            DerivedAssetId = ownAsset, SourceTimestampMs = 1200, CropX = 0, CropY = 0,
            CropWidth = 1080, CropHeight = 1080, CropMethod = "manual", SelectionMethod = "manual",
            SortOrder = 0, CreatedAt = now, UpdatedAt = now,
        });
        db.CampaignCollaborators.Add(new CampaignCollaborator
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CampaignId = campaignId,
            GrantedByUserId = userId, Email = "guest@example.com",
            NormalizedEmail = "GUEST@EXAMPLE.COM", GrantedAt = now,
        });
        db.ScheduleEntries.Add(new ScheduleEntry
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CampaignId = campaignId,
            ChannelId = "linkedin", Text = "queued post", ScheduledAt = now.AddDays(1),
            Status = "queued", CreatedAt = now, UpdatedAt = now,
        });
        db.GenerationRuns.Add(new GenerationRun
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CampaignId = campaignId,
            Status = "complete", ItemsJson = "[]", StartedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (ownAsset, sharedAsset);
    }

    private CastmillDbContext NewDb()
    {
        var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>();
        return new CastmillDbContext(options, new AllTenantsProvider());
    }

    private static async Task<CampaignResponse> CreateCampaignAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest($"Run {Guid.NewGuid():N}", null, null, null));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<CampaignResponse>())!;
    }

    private async Task<(HttpClient Client, Guid UserId, Guid TenantId)> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"del-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Deleter"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        var me = (await client.GetFromJsonAsync<MeResponse>("/api/v1/me"))!;
        return (client, me.UserId, me.TenantId);
    }

    private sealed class AllTenantsProvider : Castmill.Api.Tenancy.ITenantProvider
    {
        public Guid? TenantId => null;
    }
}
