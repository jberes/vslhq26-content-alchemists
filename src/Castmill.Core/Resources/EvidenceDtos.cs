using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Castmill.Core.Resources;

/// <summary>The immutable approved-evidence identity downstream work records.</summary>
public sealed record ApprovedEvidenceRevision(
    Guid SourceAssetId,
    int Revision,
    Guid RevisionId,
    string Hash,
    DateTimeOffset ApprovedAt);

public sealed record SourceAssetResponse(
    Guid Id,
    Guid CampaignId,
    Guid? LegacyArtifactId,
    string Kind,
    string Modality,
    string Label,
    string? OriginalUri,
    string? ContentType,
    long? SizeBytes,
    string SnapshotIdentity,
    int CurrentEvidenceRevision,
    Guid CurrentEvidenceRevisionId,
    ApprovedEvidenceRevision? ApprovedEvidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EvidenceBlockResponse(
    Guid SourceAssetId,
    string StableId,
    int Ordinal,
    string Content,
    string LocatorKind,
    JsonElement Locator,
    int Revision,
    Guid RevisionId,
    string ApprovalState,
    bool IsExcluded);

public sealed record EvidenceRevisionResponse(
    SourceAssetResponse Source,
    int Revision,
    Guid RevisionId,
    bool IsApproved,
    IReadOnlyList<EvidenceBlockResponse> Blocks);

public sealed record EvidenceBlockRevisionRequest(
    [property: MaxLength(400_000)] string? Content,
    bool? IsExcluded);

public sealed record WebPageSourceImportRequest(
    [property: Required, MaxLength(2000)] string Url,
    [property: MaxLength(300)] string? Label = null);

public sealed record DocumentSourceImportRequest(
    [property: Required] Guid AssetId,
    [property: MaxLength(300)] string? Label = null);

public sealed record ArtifactSourceImportRequest(
    [property: Required] Guid ArtifactId,
    Guid? RevisionId = null,
    [property: MaxLength(300)] string? Label = null);

/// <summary>
/// Generalized citation identity. Existing artifact JSON remains a string array; callers
/// may wrap one of those strings and optionally add the source identity when it is known.
/// </summary>
public sealed record CitationReference(string EvidenceBlockId, Guid? SourceAssetId = null);

public static class CitationReferenceCodec
{
    private const string Prefix = "evidence:";

    public static string Format(Guid sourceAssetId, string evidenceBlockId) =>
        $"{Prefix}{sourceAssetId:N}:{evidenceBlockId}";

    public static bool TryParse(string value, out CitationReference reference)
    {
        reference = new CitationReference(value);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var sourceEnd = value.IndexOf(':', Prefix.Length);
        if (sourceEnd < 0
            || !Guid.TryParseExact(value.AsSpan(Prefix.Length, sourceEnd - Prefix.Length), "N", out var sourceAssetId)
            || sourceEnd == value.Length - 1)
        {
            return false;
        }

        reference = new CitationReference(value[(sourceEnd + 1)..], sourceAssetId);
        return true;
    }
}

public sealed record CitationResolutionResponse(
    CitationReference Reference,
    bool Resolved,
    string? SourceLabel,
    ApprovedEvidenceRevision? ApprovedEvidence,
    EvidenceBlockResponse? Evidence);