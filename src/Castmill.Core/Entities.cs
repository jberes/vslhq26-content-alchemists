namespace Castmill.Core;

/// <summary>
/// Every user owns exactly one tenant, created at registration (ADR-011).
/// Tenant isolation is structural: all tenant-scoped entities carry TenantId
/// and are covered by EF global query filters (G1).
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

public sealed class Campaign : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Single-owner model (ADR-011): the Identity user who created the campaign.</summary>
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Brief { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Artifact : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    /// <summary>Typed JSON content (ADR-003); schema-validated at the boundary before persist.</summary>
    public required string ContentJson { get; set; }
    /// <summary>Optimistic-concurrency counter surfaced to clients as an ETag.</summary>
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Asset : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string BlobPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BrandProfile : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public string? StyleCardJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Per-user setting. Values of secret kinds (Foundry key, broker token) are stored
/// AES-256-GCM encrypted (Phase B3); plaintext secret values must never reach this row.
/// </summary>
public sealed class UserSetting : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public bool IsEncrypted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Security-relevant events: sign-in, password change, token revocation, publish.</summary>
public sealed class AuditEvent : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string Action { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
