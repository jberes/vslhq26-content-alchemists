using System.Net.Http.Json;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<CastmillApiFactory>;

[Collection("api")]
public sealed class TenantIsolationTests(CastmillApiFactory factory)
{
    private sealed class FixedTenantProvider(Guid? tenantId) : ITenantProvider
    {
        public Guid? TenantId => tenantId;
    }

    private static CastmillDbContext CreateContextForTenant(IServiceScope scope, Guid? tenantId) =>
        new(scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>(),
            new FixedTenantProvider(tenantId));

    /// <summary>
    /// G1 check-in gate: cross-tenant reads fail structurally — the global query
    /// filter scopes every query to the caller's tenant, and an absent tenant
    /// (unauthenticated) sees nothing at all.
    /// </summary>
    [Fact]
    public async Task Campaigns_are_invisible_across_tenants()
    {
        var client = factory.CreateClient();

        async Task<MeResponse> RegisterAsync(string name)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/register",
                new RegisterRequest($"{name}-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", name));
            response.EnsureSuccessStatusCode();
            var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
            using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            me.Headers.Authorization = new("Bearer", tokens!.AccessToken);
            var meResponse = await client.SendAsync(me);
            return (await meResponse.Content.ReadFromJsonAsync<MeResponse>())!;
        }

        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");

        using var scope = factory.CreateDbScope();

        // Seed a campaign in Alice's tenant.
        var campaignId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var aliceDb = CreateContextForTenant(scope, alice.TenantId))
        {
            aliceDb.Campaigns.Add(new Campaign
            {
                Id = campaignId,
                TenantId = alice.TenantId,
                OwnerId = alice.UserId,
                Name = "Alice's launch",
                CreatedAt = now,
                UpdatedAt = now,
            });
            aliceDb.Artifacts.Add(new Artifact
            {
                Id = artifactId,
                TenantId = alice.TenantId,
                CampaignId = campaignId,
                Kind = "blog",
                Title = "Alice's private draft",
                ContentJson = "{}",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            aliceDb.SourceAssets.Add(new SourceAsset
            {
                Id = sourceId,
                TenantId = alice.TenantId,
                CampaignId = campaignId,
                Kind = SourceKinds.Transcript,
                Modality = SourceModalities.Media,
                Label = "Alice's recording",
                SnapshotIdentity = $"sha256:{new string('a', 64)}",
                SnapshotHash = new string('a', 64),
                CurrentEvidenceRevision = 1,
                CurrentEvidenceRevisionId = revisionId,
                ApprovedEvidenceRevision = 1,
                ApprovedEvidenceRevisionId = revisionId,
                ApprovedEvidenceHash = new string('b', 64),
                ApprovedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            aliceDb.EvidenceBlocks.Add(new EvidenceBlock
            {
                Id = Guid.NewGuid(),
                TenantId = alice.TenantId,
                CampaignId = campaignId,
                SourceAssetId = sourceId,
                StableId = "s01",
                Ordinal = 0,
                Content = "Alice's private evidence.",
                ContentHash = new string('c', 64),
                LocatorKind = EvidenceLocatorKinds.MediaTimeRange,
                LocatorJson = """{"startSeconds":0,"endSeconds":4,"sourceLabel":"alice.mp4"}""",
                Revision = 1,
                RevisionId = revisionId,
                ApprovalState = EvidenceApprovalStates.Approved,
                CreatedAt = now,
                UpdatedAt = now,
            });
            aliceDb.ContentDependencySnapshots.Add(new ContentDependencySnapshot
            {
                Id = snapshotId,
                TenantId = alice.TenantId,
                CampaignId = campaignId,
                ArtifactId = artifactId,
                IsCurrent = true,
                Reason = ContentDependencyReasons.Generated,
                CreatedAt = now,
            });
            aliceDb.ContentEvidenceDependencies.Add(new ContentEvidenceDependency
            {
                TenantId = alice.TenantId,
                CampaignId = campaignId,
                SnapshotId = snapshotId,
                SourceAssetId = sourceId,
                Revision = 1,
                RevisionId = revisionId,
                Hash = new string('b', 64),
                ApprovedAt = now,
            });
            await aliceDb.SaveChangesAsync();
        }

        await using (var aliceDb = CreateContextForTenant(scope, alice.TenantId))
        {
            Assert.Equal(1, await aliceDb.Campaigns.CountAsync());
            Assert.Equal(1, await aliceDb.SourceAssets.CountAsync());
            Assert.Equal(1, await aliceDb.EvidenceBlocks.CountAsync());
            Assert.Equal(1, await aliceDb.ContentDependencySnapshots.CountAsync());
            Assert.Equal(1, await aliceDb.ContentEvidenceDependencies.CountAsync());
        }

        // Bob's tenant sees nothing; no tenant sees nothing.
        await using (var bobDb = CreateContextForTenant(scope, bob.TenantId))
        {
            Assert.Equal(0, await bobDb.Campaigns.CountAsync());
            Assert.Equal(0, await bobDb.SourceAssets.CountAsync());
            Assert.Equal(0, await bobDb.EvidenceBlocks.CountAsync());
            Assert.Equal(0, await bobDb.ContentDependencySnapshots.CountAsync());
            Assert.Equal(0, await bobDb.ContentEvidenceDependencies.CountAsync());
        }

        await using (var anonDb = CreateContextForTenant(scope, null))
        {
            Assert.Equal(0, await anonDb.Campaigns.CountAsync());
            Assert.Equal(0, await anonDb.SourceAssets.CountAsync());
            Assert.Equal(0, await anonDb.EvidenceBlocks.CountAsync());
            Assert.Equal(0, await anonDb.ContentDependencySnapshots.CountAsync());
            Assert.Equal(0, await anonDb.ContentEvidenceDependencies.CountAsync());
        }
    }
}
