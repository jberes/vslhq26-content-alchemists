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
            e.Property(a => a.Version).IsConcurrencyToken();
            e.HasIndex(a => new { a.TenantId, a.CampaignId });
            e.HasQueryFilter(a => a.TenantId == _tenantProvider.TenantId);
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
