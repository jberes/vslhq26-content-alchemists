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

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ArtifactRevision> ArtifactRevisions => Set<ArtifactRevision>();
    public DbSet<ImageSlot> ImageSlots => Set<ImageSlot>();
    public DbSet<ScheduleEntry> ScheduleEntries => Set<ScheduleEntry>();
    public DbSet<GenerationRun> GenerationRuns => Set<GenerationRun>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ClipJob> ClipJobs => Set<ClipJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Tenant>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
        });

        builder.Entity<Campaign>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.HasIndex(c => new { c.TenantId, c.UpdatedAt });
            // Structural tenant isolation (G1): every query is filtered to the
            // caller's tenant; there is no code path that opts out per-request.
            e.HasQueryFilter(c => c.TenantId == _tenantProvider.TenantId);
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
            // The Front Page's review queue filters by status across the whole tenant.
            e.HasIndex(a => new { a.TenantId, a.Status });
            e.HasQueryFilter(a => a.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<ArtifactRevision>(e =>
        {
            e.Property(r => r.Title).HasMaxLength(300);
            e.Property(r => r.Reason).HasMaxLength(50);
            e.HasIndex(r => new { r.TenantId, r.ArtifactId, r.Version });
            e.HasQueryFilter(r => r.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<ImageSlot>(e =>
        {
            e.Property(s => s.Kind).HasMaxLength(50);
            e.Property(s => s.Prompt).HasMaxLength(4000);
            e.Property(s => s.ModelAlias).HasMaxLength(100);
            e.Property(s => s.SourceSegmentId).HasMaxLength(50);
            e.Property(s => s.HeadlineText).HasMaxLength(32);
            e.Property(s => s.State).HasMaxLength(20);
            e.Property(s => s.PublishedUrl).HasMaxLength(2000);
            e.Property(s => s.BaseImagePath).HasMaxLength(1000);
            e.Property(s => s.BaseImageUrl).HasMaxLength(2000);
            // One slot per kind per campaign: reservation is idempotent by construction.
            e.HasIndex(s => new { s.TenantId, s.CampaignId, s.Kind }).IsUnique();
            e.HasQueryFilter(s => s.TenantId == _tenantProvider.TenantId);
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
            e.HasQueryFilter(s => s.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<GenerationRun>(e =>
        {
            e.Property(r => r.Status).HasMaxLength(20);
            e.HasIndex(r => new { r.TenantId, r.CampaignId, r.StartedAt });
            e.HasQueryFilter(r => r.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<Asset>(e =>
        {
            e.Property(a => a.FileName).HasMaxLength(400);
            e.Property(a => a.ContentType).HasMaxLength(200);
            e.Property(a => a.BlobPath).HasMaxLength(1000);
            e.HasQueryFilter(a => a.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<BrandProfile>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(200);
            e.HasQueryFilter(b => b.TenantId == _tenantProvider.TenantId);
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
            e.HasQueryFilter(j => j.TenantId == _tenantProvider.TenantId);
        });

        builder.Entity<RefreshToken>(e =>
        {
            e.Property(r => r.TokenHash).HasMaxLength(64);
            // Lookup is by hash of the presented token — unique so a hash
            // collision insert fails loudly instead of enabling confusion.
            e.HasIndex(r => r.TokenHash).IsUnique();
            e.HasIndex(r => new { r.UserId, r.FamilyId });
        });
    }
}
