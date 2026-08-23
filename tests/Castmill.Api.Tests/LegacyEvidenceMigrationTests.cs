using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Evidence;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class LegacyEvidenceMigrationTests(CastmillApiFactory factory)
{
    private sealed class FixedTenantProvider(Guid tenantId) : ITenantProvider
    {
        public Guid? TenantId => tenantId;
    }

    [Fact]
    public async Task Startup_backfill_uses_runtime_hashes_and_normalizes_legacy_ids()
    {
        using var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider
            .GetRequiredService<DbContextOptions<CastmillDbContext>>();
        var tenantId = Guid.NewGuid();
        await using var db = new CastmillDbContext(
            options, new FixedTenantProvider(tenantId));
        var now = DateTimeOffset.UtcNow;
        var campaignId = Guid.NewGuid();
        var repeatedId = new string('s', 120);
        var longText = "Ångström evidence " + new string('x', 4500);
        var transcript = new TranscriptContent("legacy-recorder",
        [
            new TranscriptSegment(
                repeatedId, 1.25, 7.5, "Host", longText, "recording.mp4"),
            new TranscriptSegment(
                repeatedId, 7.5, 9.0, "Guest", "Second proof.", "recording.mp4"),
        ]);
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Legacy tenant",
            CreatedAt = now,
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            TenantId = tenantId,
            OwnerId = Guid.NewGuid(),
            Name = "Untouched campaign",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Artifacts.Add(new Artifact
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CampaignId = campaignId,
            Kind = "transcript",
            Title = "Legacy transcript",
            ContentJson = System.Text.Json.JsonSerializer.Serialize(
                transcript, TranscriptService.Json),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        await using var firstDb = new CastmillDbContext(
            options, new FixedTenantProvider(tenantId));
        await using var secondDb = new CastmillDbContext(
            options, new FixedTenantProvider(tenantId));
        var results = await Task.WhenAll(
            new LegacyEvidenceBackfillService(firstDb, TimeProvider.System)
                .BackfillAsync(CancellationToken.None),
            new LegacyEvidenceBackfillService(secondDb, TimeProvider.System)
                .BackfillAsync(CancellationToken.None));
        Assert.Equal(1, results.Sum());
        Assert.Equal(0, await new LegacyEvidenceBackfillService(db, TimeProvider.System)
            .BackfillAsync(CancellationToken.None));

        var source = await db.SourceAssets.SingleAsync(item => item.CampaignId == campaignId);
        Assert.Equal(SourceKinds.Transcript, source.Kind);
        Assert.NotNull(source.LegacyArtifactId);
        Assert.NotNull(source.ApprovedEvidenceRevision);
        var blocks = await db.EvidenceBlocks
            .Where(block => block.SourceAssetId == source.Id)
            .OrderBy(block => block.Ordinal)
            .ToListAsync();
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.True(block.StableId.Length <= 100));
        Assert.Equal(2, blocks.Select(block => block.StableId).Distinct().Count());
        Assert.Equal(longText, blocks[0].Content);
        Assert.Equal(EvidenceRevisionHasher.HashApproved(blocks), source.ApprovedEvidenceHash);
        using var locator = System.Text.Json.JsonDocument.Parse(blocks[0].LocatorJson);
        Assert.Equal(1.25, locator.RootElement.GetProperty("startSeconds").GetDouble());
        Assert.Equal(7.5, locator.RootElement.GetProperty("endSeconds").GetDouble());
        Assert.Equal("recording.mp4", locator.RootElement.GetProperty("sourceLabel").GetString());
    }
}
