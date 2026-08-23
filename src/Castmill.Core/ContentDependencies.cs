namespace Castmill.Core;

/// <summary>
/// One immutable dependency observation for a research or user-content artifact. Historical
/// rows remain available when generation or an explicit acknowledgement advances the current row.
/// </summary>
public sealed class ContentDependencySnapshot : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid ArtifactId { get; set; }
    public bool IsCurrent { get; set; }
    public required string Reason { get; set; }
    public Guid? ApprovedReportArtifactId { get; set; }
    public long? ApprovedReportVersion { get; set; }
    public string? ApprovedReportHash { get; set; }
    public string? ApprovedTargetStrategyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The complete approved-evidence marker set consumed by one dependency snapshot.</summary>
public sealed class ContentEvidenceDependency : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid SourceAssetId { get; set; }
    public int Revision { get; set; }
    public Guid RevisionId { get; set; }
    public required string Hash { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
}

public static class ContentDependencyReasons
{
    public const string DeepAnalysis = "deep-analysis";
    public const string StrategyApproved = "strategy-approved";
    public const string Generated = "generated";
    public const string Regenerated = "regenerated";
    public const string Acknowledged = "acknowledged";
    public const string Restored = "restored";
}

public static class ContentStalenessStates
{
    public const string Fresh = "Fresh";
    public const string EvidenceChanged = "EvidenceChanged";
    public const string StrategyChanged = "StrategyChanged";
    public const string BothChanged = "BothChanged";
    public const string Unknown = "Unknown";
}