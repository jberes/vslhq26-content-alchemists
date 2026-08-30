using Castmill.Api.Auth;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Data;

public sealed class CastmillDbContext(
    DbContextOptions<CastmillDbContext> options,
    ITenantProvider tenantProvider)
    : IdentityDbContext<CastmillUser, IdentityRole<Guid>, Guid>(options)
{
    private readonly ITenantProvider _tenantProvider = tenantProvider;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await NormalizeCampaignAggregateTenantsAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignCollaborator> CampaignCollaborators => Set<CampaignCollaborator>();
    public DbSet<SourceAsset> SourceAssets => Set<SourceAsset>();
    public DbSet<EvidenceBlock> EvidenceBlocks => Set<EvidenceBlock>();
    public DbSet<ContentDependencySnapshot> ContentDependencySnapshots => Set<ContentDependencySnapshot>();
    public DbSet<ContentEvidenceDependency> ContentEvidenceDependencies => Set<ContentEvidenceDependency>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ArtifactRevision> ArtifactRevisions => Set<ArtifactRevision>();
    public DbSet<ImageSlot> ImageSlots => Set<ImageSlot>();
    public DbSet<ScheduleEntry> ScheduleEntries => Set<ScheduleEntry>();
    public DbSet<GenerationRun> GenerationRuns => Set<GenerationRun>();
    public DbSet<ImageVariant> ImageVariants => Set<ImageVariant>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<MediaUpload> MediaUploads => Set<MediaUpload>();
    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();
    public DbSet<BrandAsset> BrandAssets => Set<BrandAsset>();
    public DbSet<BrandTemplate> BrandTemplates => Set<BrandTemplate>();
    public DbSet<BrandCollaborator> BrandCollaborators => Set<BrandCollaborator>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ExternalAuthAttempt> ExternalAuthAttempts => Set<ExternalAuthAttempt>();
    public DbSet<ClipJob> ClipJobs => Set<ClipJob>();
    public DbSet<GitRepoProfile> GitRepoProfiles => Set<GitRepoProfile>();
    public DbSet<GitPublication> GitPublications => Set<GitPublication>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<CastmillUser>(entity =>
        {
            entity.Property(user => user.AvatarImage)
                .HasMaxLength(ExternalAvatarCaptureService.MaxAvatarBytes);
            entity.Property(user => user.AvatarContentType).HasMaxLength(50);
        });

        builder.Entity<IdentityUserLogin<Guid>>()
            .HasIndex(login => new { login.UserId, login.LoginProvider })
            .IsUnique();

        builder.Entity<Tenant>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
        });

        builder.Entity<Campaign>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Status).HasMaxLength(20).HasDefaultValue(CampaignStatus.Draft);
            e.Property(c => c.ContentType).HasMaxLength(30);
            e.Property(c => c.Intent).HasMaxLength(30);
            e.Property(c => c.OutputRecipeJson).HasMaxLength(4000);
            e.Property(c => c.ShareDomain).HasMaxLength(256);
            e.HasIndex(c => new { c.TenantId, c.UpdatedAt });
            e.HasIndex(c => new { c.TenantId, c.BrandId });
            e.HasIndex(c => c.ShareDomain);
            e.HasQueryFilter(c => c.TenantId == _tenantProvider.TenantId
                || (_tenantProvider.NormalizedEmail != null
                    && ((c.ShareDomain != null && c.ShareDomain == _tenantProvider.EmailDomain)
                        || CampaignCollaborators.Any(collaborator =>
                            collaborator.CampaignId == c.Id
                            && collaborator.NormalizedEmail == _tenantProvider.NormalizedEmail))));
        });

        builder.Entity<CampaignCollaborator>(e =>
        {
            e.Property(collaborator => collaborator.Email).HasMaxLength(256);
            e.Property(collaborator => collaborator.NormalizedEmail).HasMaxLength(256);
            e.HasIndex(collaborator => new { collaborator.CampaignId, collaborator.NormalizedEmail })
                .IsUnique();
            e.HasIndex(collaborator => collaborator.NormalizedEmail);
            e.HasOne<Campaign>()
                .WithMany()
                .HasForeignKey(collaborator => collaborator.CampaignId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(collaborator =>
                collaborator.TenantId == _tenantProvider.TenantId
                || collaborator.NormalizedEmail == _tenantProvider.NormalizedEmail);
        });

        builder.Entity<SourceAsset>(e =>
        {
            e.Property(source => source.Kind).HasMaxLength(50);
            e.Property(source => source.Modality).HasMaxLength(30);
            e.Property(source => source.Label).HasMaxLength(300);
            e.Property(source => source.OriginalUri).HasMaxLength(2000);
            e.Property(source => source.BlobPath).HasMaxLength(1000);
            e.Property(source => source.ContentType).HasMaxLength(200);
            e.Property(source => source.SnapshotIdentity).HasMaxLength(200);
            e.Property(source => source.SnapshotHash).HasMaxLength(64);
            e.Property(source => source.ApprovedEvidenceHash).HasMaxLength(64);
            e.HasIndex(source => new { source.TenantId, source.CampaignId, source.Kind });
            e.HasIndex(source => new
                { source.TenantId, source.CampaignId, source.Kind, source.SnapshotIdentity })
                .IsUnique();
            e.HasIndex(source => new { source.TenantId, source.LegacyArtifactId })
                .IsUnique()
                .HasFilter("[LegacyArtifactId] IS NOT NULL");
            e.HasOne<Campaign>()
                .WithMany()
                .HasForeignKey(source => source.CampaignId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Artifact>()
                .WithMany()
                .HasForeignKey(source => source.LegacyArtifactId)
                .OnDelete(DeleteBehavior.SetNull);
            e.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_SourceAssets_EvidenceRevision",
                    "[CurrentEvidenceRevision] >= 1 AND (("
                    + "[ApprovedEvidenceRevision] IS NULL AND "
                    + "[ApprovedEvidenceRevisionId] IS NULL AND "
                    + "[ApprovedEvidenceHash] IS NULL AND [ApprovedAt] IS NULL) OR ("
                    + "[ApprovedEvidenceRevision] IS NOT NULL AND "
                    + "[ApprovedEvidenceRevisionId] IS NOT NULL AND "
                    + "[ApprovedEvidenceHash] IS NOT NULL AND [ApprovedAt] IS NOT NULL AND "
                    + "[ApprovedEvidenceRevision] <= [CurrentEvidenceRevision]))");
                table.HasCheckConstraint(
                    "CK_SourceAssets_SizeBytes",
                    "[SizeBytes] IS NULL OR [SizeBytes] >= 0");
            });
            e.HasQueryFilter(source => source.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == source.CampaignId));
        });

        builder.Entity<EvidenceBlock>(e =>
        {
            e.Property(block => block.StableId).HasMaxLength(100);
            e.Property(block => block.ContentHash).HasMaxLength(64);
            e.Property(block => block.LocatorKind).HasMaxLength(50);
            e.Property(block => block.ApprovalState).HasMaxLength(20);
            e.HasIndex(block => new
                { block.TenantId, block.SourceAssetId, block.Revision, block.StableId })
                .IsUnique();
            e.HasIndex(block => new
                { block.TenantId, block.CampaignId, block.SourceAssetId, block.Revision, block.Ordinal });
            e.HasOne<SourceAsset>()
                .WithMany()
                .HasForeignKey(block => block.SourceAssetId)
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_EvidenceBlocks_Revision", "[Revision] >= 1");
                table.HasCheckConstraint("CK_EvidenceBlocks_Ordinal", "[Ordinal] >= 0");
                table.HasCheckConstraint(
                    "CK_EvidenceBlocks_ApprovalState",
                    "[ApprovalState] IN ('Draft', 'Approved')");
            });
            e.HasQueryFilter(block => block.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == block.CampaignId));
        });

        builder.Entity<ContentDependencySnapshot>(e =>
        {
            e.Property(snapshot => snapshot.Reason).HasMaxLength(30);
            e.Property(snapshot => snapshot.ApprovedReportHash).HasMaxLength(64);
            e.Property(snapshot => snapshot.ApprovedTargetStrategyHash).HasMaxLength(64);
            e.HasIndex(snapshot => new
                { snapshot.TenantId, snapshot.CampaignId, snapshot.ArtifactId, snapshot.IsCurrent });
            e.HasIndex(snapshot => new { snapshot.TenantId, snapshot.ArtifactId })
                .IsUnique()
                .HasFilter("[IsCurrent] = 1")
                .HasDatabaseName("UX_ContentDependencySnapshots_Current");
            e.HasIndex(snapshot => new
                { snapshot.TenantId, snapshot.ArtifactId, snapshot.CreatedAt });
            e.HasOne<Artifact>()
                .WithMany()
                .HasForeignKey(snapshot => snapshot.ArtifactId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(snapshot => snapshot.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == snapshot.CampaignId));
        });

        builder.Entity<ContentEvidenceDependency>(e =>
        {
            e.HasKey(marker => new { marker.SnapshotId, marker.SourceAssetId });
            e.Property(marker => marker.Hash).HasMaxLength(64);
            e.HasIndex(marker => new
                { marker.TenantId, marker.CampaignId, marker.SourceAssetId, marker.RevisionId });
            e.HasOne<ContentDependencySnapshot>()
                .WithMany()
                .HasForeignKey(marker => marker.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(marker => marker.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == marker.CampaignId));
        });

        builder.Entity<Artifact>(e =>
        {
            e.Property(a => a.Kind).HasMaxLength(50);
            e.Property(a => a.Title).HasMaxLength(300);
            e.Property(a => a.Status).HasMaxLength(20);
            // SQL extracts citations so list projections never load ContentJson (ADR-003).
            // ISJSON guards against a legacy non-JSON payload making the column throw.
            e.Property(a => a.CitationsJson).HasComputedColumnSql(
                "CASE WHEN ISJSON([ContentJson]) = 1 THEN COALESCE("
                + "JSON_QUERY([ContentJson], '$.citations'), "
                + "JSON_QUERY([ContentJson], '$.content.citations')) END");
            e.Property(a => a.Version).IsConcurrencyToken();
            e.HasIndex(a => new { a.TenantId, a.CampaignId });
            e.HasIndex(a => new { a.TenantId, a.ParentArtifactId });
            // The Front Page's review queue filters by status across the whole tenant.
            e.HasIndex(a => new { a.TenantId, a.Status });
            e.HasQueryFilter(a => a.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == a.CampaignId));
        });

        builder.Entity<ArtifactRevision>(e =>
        {
            e.Property(r => r.Title).HasMaxLength(300);
            e.Property(r => r.Reason).HasMaxLength(50);
            e.HasIndex(r => new { r.TenantId, r.ArtifactId, r.Version });
            e.HasIndex(r => r.ContentDependencySnapshotId);
            e.HasOne<ContentDependencySnapshot>()
                .WithMany()
                .HasForeignKey(r => r.ContentDependencySnapshotId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(r => r.TenantId == _tenantProvider.TenantId
                || Artifacts.Any(artifact => artifact.Id == r.ArtifactId));
        });

        builder.Entity<ImageSlot>(e =>
        {
            e.Property(s => s.Kind).HasMaxLength(50);
            e.Property(s => s.Prompt).HasMaxLength(4000);
            e.Property(s => s.PromptMode).HasMaxLength(10).HasDefaultValue("Auto");
            e.Property(s => s.ReferenceAssetIdsJson).HasMaxLength(4000);
            e.Property(s => s.ModelAlias).HasMaxLength(100);
            e.Property(s => s.SourceSegmentId).HasMaxLength(50);
            e.Property(s => s.HeadlineText).HasMaxLength(32);
            e.Property(s => s.HeadlineBackground).HasMaxLength(9);
            e.Property(s => s.State).HasMaxLength(20);
            e.Property(s => s.PublishedUrl).HasMaxLength(2000);
            e.Property(s => s.BaseImagePath).HasMaxLength(1000);
            e.Property(s => s.BaseImageUrl).HasMaxLength(2000);
            // One slot per kind per ARTIFACT, and one per campaign for the artifact-less
            // kinds. Widened from (Tenant, Campaign, Kind): that made blog-header unique per
            // campaign, so a second blog could never own its own header image — its prompts
            // were silently skipped because the campaign's slot was already filled.
            //
            // TWO indexes, not one. SQL Server treats NULLs as equal in a unique index, so a
            // single index over the nullable ArtifactId would reject a second campaign-wide
            // slot of a different kind; adding IS NOT NULL to escape that would leave the
            // campaign-wide rows unconstrained entirely — which is exactly the idempotent
            // reservation this index exists to guarantee.
            e.HasIndex(s => new { s.TenantId, s.CampaignId, s.ArtifactId, s.Kind })
                .IsUnique()
                .HasFilter("[ArtifactId] IS NOT NULL")
                .HasDatabaseName("IX_ImageSlots_Tenant_Campaign_Artifact_Kind");
            e.HasIndex(s => new { s.TenantId, s.CampaignId, s.Kind })
                .IsUnique()
                .HasFilter("[ArtifactId] IS NULL")
                .HasDatabaseName("IX_ImageSlots_Tenant_Campaign_Kind_NoArtifact");
            e.HasQueryFilter(s => s.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == s.CampaignId));
        });

        builder.Entity<GitRepoProfile>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Owner).HasMaxLength(100);
            e.Property(p => p.Repo).HasMaxLength(100);
            e.Property(p => p.BaseBranch).HasMaxLength(255);
            e.Property(p => p.Preset).HasMaxLength(20);
            e.Property(p => p.Mode).HasMaxLength(20);
            e.HasIndex(p => new { p.TenantId, p.BrandId });
            e.HasQueryFilter(p => p.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<GitPublication>(e =>
        {
            e.Property(p => p.Branch).HasMaxLength(255);
            e.Property(p => p.CommitSha).HasMaxLength(64);
            e.Property(p => p.PullRequestUrl).HasMaxLength(500);
            e.Property(p => p.Status).HasMaxLength(20);
            e.Property(p => p.ContentPath).HasMaxLength(500);
            // Re-publishing an artifact to the same repo updates this row rather than
            // opening a second branch and a second pull request.
            e.HasIndex(p => new { p.TenantId, p.ArtifactId, p.RepoProfileId }).IsUnique();
            e.HasQueryFilter(p => p.TenantId == _tenantProvider.TenantId
                || Artifacts.Any(artifact => artifact.Id == p.ArtifactId));
        });

        builder.Entity<ScheduleEntry>(e =>
        {
            e.Property(s => s.ChannelId).HasMaxLength(200);
            e.Property(s => s.BrokerPostId).HasMaxLength(200);
            e.Property(s => s.Text).HasMaxLength(65_000);
            e.Property(s => s.MediaUrl).HasMaxLength(2000);
            e.Property(s => s.Status).HasMaxLength(20);
            e.Property(s => s.Error).HasMaxLength(2000);
            e.HasIndex(s => new { s.TenantId, s.ScheduledAt });
            e.HasQueryFilter(s => s.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == s.CampaignId));
        });

        builder.Entity<GenerationRun>(e =>
        {
            e.Property(r => r.Status).HasMaxLength(20);
            e.Property(r => r.Kind).HasMaxLength(20).HasDefaultValue("content");
            e.HasIndex(r => new { r.TenantId, r.CampaignId, r.StartedAt });
            e.HasQueryFilter(r => r.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == r.CampaignId));
        });

        builder.Entity<ImageVariant>(e =>
        {
            e.Property(v => v.Url).HasMaxLength(2000);
            e.Property(v => v.BlobPath).HasMaxLength(1000);
            e.Property(v => v.ThumbUrl).HasMaxLength(2000);
            e.Property(v => v.ThumbBlobPath).HasMaxLength(1000);
            e.Property(v => v.Model).HasMaxLength(100);
            e.Property(v => v.Prompt).HasMaxLength(8000);
            e.Property(v => v.SteeringNote).HasMaxLength(1000);
            e.Property(v => v.State).HasMaxLength(20);
            e.HasIndex(v => new { v.TenantId, v.SlotId, v.CreatedAt });
            e.HasQueryFilter(v => v.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == v.CampaignId));
        });

        builder.Entity<Asset>(e =>
        {
            e.Property(a => a.FileName).HasMaxLength(400);
            e.Property(a => a.ContentType).HasMaxLength(200);
            e.Property(a => a.BlobPath).HasMaxLength(1000);
            e.HasQueryFilter(a => a.TenantId == _tenantProvider.TenantId
                || MediaUploads.Any(upload => upload.AssetId == a.Id));
        });

        builder.Entity<MediaUpload>(e =>
        {
            e.Property(upload => upload.BlockIdsJson).HasMaxLength(80_000);
            e.Property(upload => upload.Status).HasMaxLength(20);
            e.Property(upload => upload.Error).HasMaxLength(2000);
            e.HasIndex(upload => new { upload.TenantId, upload.CampaignId, upload.UpdatedAt });
            e.HasOne<Campaign>()
                .WithMany()
                .HasForeignKey(upload => upload.CampaignId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Asset>()
                .WithMany()
                .HasForeignKey(upload => upload.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
            e.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_MediaUploads_Progress",
                    "[UploadedBytes] >= 0 AND [NextBlockIndex] >= 0");
                table.HasCheckConstraint(
                    "CK_MediaUploads_Status",
                    "[Status] IN ('Uploading', 'Committed', 'Transcribing', 'Completed', 'Cancelled')");
            });
            e.HasQueryFilter(upload => upload.TenantId == _tenantProvider.TenantId
                || Campaigns.Any(campaign => campaign.Id == upload.CampaignId));
        });

        builder.Entity<BrandProfile>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(200);
            e.HasQueryFilter(b => b.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<BrandCollaborator>(entity =>
        {
            entity.Property(collaborator => collaborator.Email).HasMaxLength(256);
            entity.HasIndex(collaborator => new { collaborator.BrandId, collaborator.UserId })
                .IsUnique();
            entity.HasIndex(collaborator => collaborator.UserId);
            entity.HasOne<BrandProfile>()
                .WithMany()
                .HasForeignKey(collaborator => collaborator.BrandId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CastmillUser>()
                .WithMany()
                .HasForeignKey(collaborator => collaborator.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(collaborator =>
                collaborator.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<BrandAsset>(e =>
        {
            e.Property(a => a.Kind).HasMaxLength(20);
            e.Property(a => a.Label).HasMaxLength(200);
            e.HasIndex(a => new { a.TenantId, a.BrandId });
            e.HasIndex(a => new { a.TenantId, a.BrandId, a.AssetId }).IsUnique();
            e.HasQueryFilter(a => a.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<BrandTemplate>(e =>
        {
            e.Property(t => t.Kind).HasMaxLength(50);
            e.Property(t => t.Name).HasMaxLength(200);
            // Brand templates are full content briefs, not short style hints. A practical
            // YouTube strategy prompt is commonly 7–10K characters.
            e.Property(t => t.SteeringPrompt).HasMaxLength(20000);
            e.HasIndex(t => new { t.TenantId, t.BrandId, t.Kind, t.Name }).IsUnique();
            e.HasQueryFilter(t => t.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<UserSetting>(e =>
        {
            e.Property(s => s.Key).HasMaxLength(100);
            e.HasIndex(s => new { s.TenantId, s.UserId, s.Key }).IsUnique();
            e.HasQueryFilter(s => s.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<AuditEvent>(e =>
        {
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.Detail).HasMaxLength(2000);
            e.HasIndex(a => new { a.TenantId, a.OccurredAt });
            e.HasQueryFilter(a => a.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<ClipJob>(e =>
        {
            e.Property(j => j.Mode).HasMaxLength(10);
            e.Property(j => j.Status).HasMaxLength(20);
            e.Property(j => j.OutputBlobPath).HasMaxLength(1000);
            e.Property(j => j.Error).HasMaxLength(2000);
            e.Property(j => j.CallbackTokenHash).HasMaxLength(64);
            e.HasIndex(j => new { j.TenantId, j.CreatedAt });
            e.HasQueryFilter(j => j.TenantId == _tenantProvider.TenantId
                || Assets.Any(asset => asset.Id == j.AssetId));
        });

        builder.Entity<RefreshToken>(e =>
        {
            e.Property(r => r.TokenHash).HasMaxLength(64);
            // Lookup is by hash of the presented token — unique so a hash
            // collision insert fails loudly instead of enabling confusion.
            e.HasIndex(r => r.TokenHash).IsUnique();
            e.HasIndex(r => new { r.UserId, r.FamilyId });
        });

        builder.Entity<ExternalAuthAttempt>(e =>
        {
            e.Property(a => a.Provider).HasMaxLength(20);
            e.Property(a => a.ClientKind).HasMaxLength(20);
            e.Property(a => a.ReturnRouteKey).HasMaxLength(50);
            e.Property(a => a.CodeChallenge).HasMaxLength(128);
            e.Property(a => a.PollSecretHash).HasMaxLength(64);
            e.Property(a => a.ExchangeCodeHash).HasMaxLength(64);
            e.Property(a => a.CandidateProviderKey).HasMaxLength(256);
            e.Property(a => a.CandidateEmail).HasMaxLength(320);
            e.Property(a => a.CandidateDisplayName).HasMaxLength(200);
            e.Property(a => a.CandidateAvatarImage)
                .HasMaxLength(ExternalAvatarCaptureService.MaxAvatarBytes);
            e.Property(a => a.CandidateAvatarContentType).HasMaxLength(50);
            e.Property(a => a.LoopbackReturnUri).HasMaxLength(512);
            e.Property(a => a.Status).HasMaxLength(20);
            e.Property(a => a.ErrorCode).HasMaxLength(100);
            e.HasIndex(a => a.ExchangeCodeHash).IsUnique();
            e.HasIndex(a => new { a.Status, a.ExpiresAt });
            e.HasOne<CastmillUser>()
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CastmillUser>()
                .WithMany()
                .HasForeignKey(a => a.LinkUserId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    private async Task NormalizeCampaignAggregateTenantsAsync(CancellationToken ct)
    {
        var directEntries = ChangeTracker.Entries<ITenantScoped>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => new
            {
                Entry = entry,
                CampaignId = entry.Entity switch
                {
                    SourceAsset entity => entity.CampaignId,
                    EvidenceBlock entity => entity.CampaignId,
                    ContentDependencySnapshot entity => entity.CampaignId,
                    ContentEvidenceDependency entity => entity.CampaignId,
                    Artifact entity => entity.CampaignId,
                    ImageSlot entity => entity.CampaignId,
                    ScheduleEntry entity => entity.CampaignId,
                    GenerationRun entity => entity.CampaignId,
                    ImageVariant entity => entity.CampaignId,
                    MediaUpload entity => entity.CampaignId,
                    _ => (Guid?)null,
                },
            })
            .Where(item => item.CampaignId is not null)
            .ToList();

        var artifactEntries = ChangeTracker.Entries<ITenantScoped>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => new
            {
                Entry = entry,
                ArtifactId = entry.Entity switch
                {
                    ArtifactRevision entity => entity.ArtifactId,
                    GitPublication entity => entity.ArtifactId,
                    _ => (Guid?)null,
                },
            })
            .Where(item => item.ArtifactId is not null)
            .ToList();

        var artifactIds = artifactEntries.Select(item => item.ArtifactId!.Value).Distinct().ToList();
        var artifactCampaigns = artifactIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await Artifacts.IgnoreQueryFilters().AsNoTracking()
                .Where(artifact => artifactIds.Contains(artifact.Id))
                .ToDictionaryAsync(artifact => artifact.Id, artifact => artifact.CampaignId, ct);

        var campaignIds = directEntries.Select(item => item.CampaignId!.Value)
            .Concat(artifactEntries
                .Select(item => artifactCampaigns.GetValueOrDefault(item.ArtifactId!.Value))
                .Where(id => id != Guid.Empty))
            .Distinct()
            .ToList();
        if (campaignIds.Count == 0)
        {
            return;
        }

        var campaignTenants = ChangeTracker.Entries<Campaign>()
            .Where(entry => entry.State != EntityState.Deleted && campaignIds.Contains(entry.Entity.Id))
            .ToDictionary(entry => entry.Entity.Id, entry => entry.Entity.TenantId);
        var missingCampaignIds = campaignIds.Where(id => !campaignTenants.ContainsKey(id)).ToList();
        if (missingCampaignIds.Count > 0)
        {
            var storedTenants = await Campaigns.IgnoreQueryFilters().AsNoTracking()
                .Where(campaign => missingCampaignIds.Contains(campaign.Id))
                .ToDictionaryAsync(campaign => campaign.Id, campaign => campaign.TenantId, ct);
            foreach (var (campaignId, tenantId) in storedTenants)
            {
                campaignTenants[campaignId] = tenantId;
            }
        }

        foreach (var item in directEntries)
        {
            if (campaignTenants.TryGetValue(item.CampaignId!.Value, out var tenantId))
            {
                item.Entry.Entity.TenantId = tenantId;
            }
        }
        foreach (var item in artifactEntries)
        {
            if (artifactCampaigns.TryGetValue(item.ArtifactId!.Value, out var campaignId)
                && campaignTenants.TryGetValue(campaignId, out var tenantId))
            {
                item.Entry.Entity.TenantId = tenantId;
            }
        }

        var mediaUploadTenants = ChangeTracker.Entries<MediaUpload>()
            .Where(entry => entry.State == EntityState.Added)
            .Where(entry => campaignTenants.ContainsKey(entry.Entity.CampaignId))
            .ToDictionary(entry => entry.Entity.AssetId,
                entry => campaignTenants[entry.Entity.CampaignId]);
        foreach (var assetEntry in ChangeTracker.Entries<Asset>()
            .Where(entry => entry.State == EntityState.Added))
        {
            if (mediaUploadTenants.TryGetValue(assetEntry.Entity.Id, out var tenantId))
            {
                assetEntry.Entity.TenantId = tenantId;
            }
        }
    }
}
