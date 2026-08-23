namespace Castmill.Core.Resources;

public sealed record ContentDependencyIdentity(
    IReadOnlyList<ApprovedEvidenceRevision> Evidence,
    Guid? ReportArtifactId,
    long? ReportVersion,
    string? ReportHash,
    string? TargetStrategyHash);

public sealed record ContentImpactReason(string Code, string Detail);

public sealed record ContentImpactItemResponse(
    Guid ArtifactId,
    string Kind,
    string Title,
    string State,
    IReadOnlyList<ContentImpactReason> Reasons,
    ContentDependencyIdentity? Prior,
    ContentDependencyIdentity Current,
    bool CanAcknowledge,
    bool CanRegenerate,
    string? ReadinessReason);

public sealed record ContentImpactReviewResponse(
    Guid CampaignId,
    IReadOnlyList<ContentImpactItemResponse> Artifacts);

public sealed record ContentImpactActionResponse(
    Guid ArtifactId,
    string Action,
    ContentImpactItemResponse Impact);