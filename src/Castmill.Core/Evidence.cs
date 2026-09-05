namespace Castmill.Core;

/// <summary>An immutable source snapshot owned by one campaign.</summary>
public sealed class SourceAsset : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid? LegacyArtifactId { get; set; }
    public required string Kind { get; set; }
    public required string Modality { get; set; }
    public required string Label { get; set; }
    public string? OriginalUri { get; set; }
    public string? BlobPath { get; set; }
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
    /// <summary>
    /// Where the recording lives on the machine that transcribed it (ADR-057). Opaque to the
    /// server — never dereferenced here; the desktop shell uses it to play and re-link.
    /// </summary>
    public string? LocalPath { get; set; }
    /// <summary>Cheap content fingerprint (size + head/tail SHA-256) so a moved file can be re-linked with confidence.</summary>
    public string? ContentHash { get; set; }
    /// <summary>The optional cloud copy: an Asset in the private container, uploaded on request.</summary>
    public Guid? MediaAssetId { get; set; }
    public required string SnapshotIdentity { get; set; }
    public required string SnapshotHash { get; set; }
    public int CurrentEvidenceRevision { get; set; }
    public Guid CurrentEvidenceRevisionId { get; set; }
    public int? ApprovedEvidenceRevision { get; set; }
    public Guid? ApprovedEvidenceRevisionId { get; set; }
    public string? ApprovedEvidenceHash { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A stable citation target within one versioned source evidence set.</summary>
public sealed class EvidenceBlock : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid SourceAssetId { get; set; }
    public required string StableId { get; set; }
    public int Ordinal { get; set; }
    public required string Content { get; set; }
    public required string ContentHash { get; set; }
    public required string LocatorKind { get; set; }
    public required string LocatorJson { get; set; }
    public int Revision { get; set; }
    public Guid RevisionId { get; set; }
    public required string ApprovalState { get; set; }
    public bool IsExcluded { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SourceKinds
{
    public const string Transcript = "transcript";
    public const string WebPage = "webpage";
    public const string Document = "document";
    public const string CastmillArtifact = "castmill-artifact";
}

public static class SourceModalities
{
    public const string Media = "media";
    public const string Text = "text";
    public const string Web = "web";
    public const string Document = "document";
    public const string Artifact = "artifact";
}

public static class EvidenceLocatorKinds
{
    public const string MediaTimeRange = "media-time-range";
    public const string TextSegment = "text-segment";
    public const string WebPageMetadata = "webpage-metadata";
    public const string WebPageImage = "webpage-image";
    public const string WebPageSection = "webpage-section";
    public const string DocumentSection = "document-section";
    public const string Slide = "slide";
    public const string ArtifactField = "artifact-field";
}

public static class EvidenceApprovalStates
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
}