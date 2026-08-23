using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Evidence;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

public sealed class ContentDependencyServiceTests
{
    [Fact]
    public void Classifier_returns_every_staleness_state_from_immutable_identities()
    {
        var marker = Marker(1, "aa");
        var prior = Identity([marker], "report-a", "targets-a");

        Assert.Equal(ContentStalenessStates.Fresh,
            ContentDependencyService.Classify(prior, Identity([marker], "report-a", "targets-a")));
        Assert.Equal(ContentStalenessStates.EvidenceChanged,
            ContentDependencyService.Classify(prior, Identity([Marker(2, "bb")], "report-a", "targets-a")));
        Assert.Equal(ContentStalenessStates.StrategyChanged,
            ContentDependencyService.Classify(prior, Identity([marker], "report-b", "targets-a")));
        Assert.Equal(ContentStalenessStates.BothChanged,
            ContentDependencyService.Classify(prior, Identity([Marker(2, "bb")], "report-b", "targets-a")));
        Assert.Equal(ContentStalenessStates.Unknown,
            ContentDependencyService.Classify(null, Identity([marker], "report-a", "targets-a")));
        Assert.Equal(ContentStalenessStates.Unknown,
            ContentDependencyService.Classify(Identity([], null, null), Identity([marker], "report-a", "targets-a")));
    }

    [Fact]
    public void Report_strategy_hash_ignores_operational_status_and_share_state()
    {
        var report = Report();
        var first = ContentDependencyService.HashReportStrategy(
            JsonSerializer.Serialize(report, WebJson));
        var operationalChange = ContentDependencyService.HashReportStrategy(
            JsonSerializer.Serialize(report with
            {
                Status = "Approved",
                InputsStale = true,
                AnglesStale = true,
                ShareStale = true,
                SharedAt = DateTimeOffset.UnixEpoch,
            }, WebJson));
        var strategyChange = ContentDependencyService.HashReportStrategy(
            JsonSerializer.Serialize(report with
            {
                Recommendations = ["Use a different strategy."],
            }, WebJson));

        Assert.Equal(first, operationalChange);
        Assert.NotEqual(first, strategyChange);
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static ApprovedEvidenceRevision Marker(int revision, string hash) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        revision,
        Guid.Parse($"{revision:D8}-2222-2222-2222-222222222222"),
        hash,
        DateTimeOffset.UnixEpoch.AddMinutes(revision));

    private static ContentDependencyIdentity Identity(
        IReadOnlyList<ApprovedEvidenceRevision> evidence,
        string? reportHash,
        string? targetHash) =>
        new(evidence, Guid.Parse("33333333-3333-3333-3333-333333333333"), 4,
            reportHash, targetHash);

    private static SeoAnalysisReportResponse Report() => new(
        Guid.NewGuid(),
        DateTimeOffset.UnixEpoch,
        new SeoResearchResponse([new SeoTarget("grid")], [], false, []),
        new SeoSerpSnapshot("grid", null, null, []),
        ["Use the source-backed angle."]);
}

[Collection("api")]
public sealed class ContentDependencyCaptureTests(CastmillApiFactory factory)
{
    private sealed class FixedTenantProvider(Guid tenantId) : ITenantProvider
    {
        public Guid? TenantId => tenantId;
    }

    [Fact]
    public async Task Capture_uses_the_evidence_marker_loaded_before_generation()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var tenantId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Marker tenant", CreatedAt = now });
        var campaign = new Campaign
        {
            Id = campaignId,
            TenantId = tenantId,
            OwnerId = Guid.NewGuid(),
            Name = "Marker campaign",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var artifact = new Artifact
        {
            Id = artifactId,
            TenantId = tenantId,
            CampaignId = campaignId,
            Kind = "blog",
            Title = "Generated before approval changed",
            ContentJson = "{}",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Campaigns.Add(campaign);
        db.Artifacts.Add(artifact);
        db.SourceAssets.Add(new SourceAsset
        {
            Id = sourceId,
            TenantId = tenantId,
            CampaignId = campaignId,
            Kind = SourceKinds.Transcript,
            Modality = SourceModalities.Media,
            Label = "advanced source",
            SnapshotIdentity = "sha256:advanced",
            SnapshotHash = "advanced",
            CurrentEvidenceRevision = 2,
            CurrentEvidenceRevisionId = Guid.NewGuid(),
            ApprovedEvidenceRevision = 2,
            ApprovedEvidenceRevisionId = Guid.NewGuid(),
            ApprovedEvidenceHash = "current-hash",
            ApprovedAt = now.AddMinutes(1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var promptedMarker = new ApprovedEvidenceRevision(
            sourceId, 1, Guid.NewGuid(), "prompted-hash", now);
        var service = new ContentDependencyService(db, TimeProvider.System);
        await service.CaptureGeneratedAsync(
            artifact,
            campaign,
            ContentDependencyReasons.Generated,
            CancellationToken.None,
            [promptedMarker]);

        var snapshot = await db.ContentDependencySnapshots.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.ArtifactId == artifactId && candidate.IsCurrent);
        var captured = await db.ContentEvidenceDependencies.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.SnapshotId == snapshot.Id);
        Assert.Equal(promptedMarker.Revision, captured.Revision);
        Assert.Equal(promptedMarker.RevisionId, captured.RevisionId);
        Assert.Equal(promptedMarker.Hash, captured.Hash);
        Assert.Equal(promptedMarker.ApprovedAt, captured.ApprovedAt);
    }

    [Fact]
    public async Task Concurrent_captures_leave_exactly_one_current_snapshot()
    {
        var tenantId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var seedScope = factory.CreateDbScope();
        var options = seedScope.ServiceProvider
            .GetRequiredService<DbContextOptions<CastmillDbContext>>();
        await using (var seed = new CastmillDbContext(options, new FixedTenantProvider(tenantId)))
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Concurrent marker tenant",
                CreatedAt = now,
            });
            seed.Campaigns.Add(new Campaign
            {
                Id = campaignId,
                TenantId = tenantId,
                OwnerId = Guid.NewGuid(),
                Name = "Concurrent marker campaign",
                CreatedAt = now,
                UpdatedAt = now,
            });
            seed.Artifacts.Add(new Artifact
            {
                Id = artifactId,
                TenantId = tenantId,
                CampaignId = campaignId,
                Kind = "blog",
                Title = "Concurrent capture",
                ContentJson = "{}",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        async Task CaptureAsync(string reason)
        {
            await using var context = new CastmillDbContext(
                options, new FixedTenantProvider(tenantId));
            var campaign = await context.Campaigns.SingleAsync(item => item.Id == campaignId);
            var artifact = await context.Artifacts.SingleAsync(item => item.Id == artifactId);
            var service = new ContentDependencyService(context, TimeProvider.System);
            await service.CaptureGeneratedAsync(
                artifact, campaign, reason, CancellationToken.None, []);
        }

        await Task.WhenAll(
            CaptureAsync(ContentDependencyReasons.Generated),
            CaptureAsync(ContentDependencyReasons.Regenerated));

        await using var verify = new CastmillDbContext(
            options, new FixedTenantProvider(tenantId));
        var snapshots = await verify.ContentDependencySnapshots
            .Where(snapshot => snapshot.ArtifactId == artifactId)
            .ToListAsync();
        Assert.Equal(2, snapshots.Count);
        Assert.Single(snapshots, snapshot => snapshot.IsCurrent);
    }
}