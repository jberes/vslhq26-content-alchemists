using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Resources;

public sealed record VideoReferenceCropDto(
    [property: Range(0, int.MaxValue)] int X,
    [property: Range(0, int.MaxValue)] int Y,
    [property: Range(1, int.MaxValue)] int Width,
    [property: Range(1, int.MaxValue)] int Height,
    [property: Required, MaxLength(20)] string Method,
    [property: Range(0, 1)] double Confidence = 1);

public sealed record VideoReferenceImageCreateRequest(
    Guid SourceFrameAssetId,
    Guid DerivedAssetId,
    [property: Range(0, long.MaxValue)] long SourceTimestampMs,
    [property: Range(0, long.MaxValue)] long? SourceFrameNumber,
    VideoReferenceCropDto Crop,
    [property: Required, MaxLength(30)] string SelectionMethod,
    [property: MaxLength(32)] string? PerceptualHash,
    [property: Range(0, 100)] double? QualityScore,
    [property: MaxLength(1000)] string? AiSummary,
    [property: MaxLength(200)] string? Label,
    [property: Range(0, 100)] int SortOrder);

public sealed record VideoReferenceSetCreateRequest(
    Guid VideoAssetId,
    Guid BrandId,
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: MaxLength(500)] string? Purpose,
    [property: Required, MaxLength(30)] string SelectionGoal,
    [property: Range(1, long.MaxValue)] long DurationMs,
    [property: Range(1, 20000)] int SourceWidth,
    [property: Range(1, 20000)] int SourceHeight,
    [property: Range(.01, 1000)] double FrameRate,
    [property: MinLength(1), MaxLength(30)] IReadOnlyList<VideoReferenceImageCreateRequest> Images);

public sealed record VideoReferenceImageResponse(
    Guid Id,
    Guid ReferenceSetId,
    Guid VideoAssetId,
    Guid SourceFrameAssetId,
    Guid DerivedAssetId,
    long SourceTimestampMs,
    long? SourceFrameNumber,
    VideoReferenceCropDto Crop,
    string SelectionMethod,
    string? PerceptualHash,
    double? QualityScore,
    string? AiSummary,
    string? Label,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VideoReferenceSetResponse(
    Guid Id,
    Guid BrandId,
    Guid CampaignId,
    Guid VideoAssetId,
    string Name,
    string? Purpose,
    string SelectionGoal,
    long DurationMs,
    int SourceWidth,
    int SourceHeight,
    double FrameRate,
    DateTimeOffset CreatedAt,
    IReadOnlyList<VideoReferenceImageResponse> Images);

public sealed record VideoReferenceImageUpdateRequest(
    Guid DerivedAssetId,
    VideoReferenceCropDto Crop,
    [property: MaxLength(200)] string? Label = null);

public sealed record VideoReferenceAiCandidate(
    [property: Required, MaxLength(80)] string Id,
    [property: Range(0, double.MaxValue)] double TimestampSeconds,
    [property: Required, MaxLength(2_000_000)] string JpegBase64,
    [property: Range(0, 100)] double QualityScore);

public sealed record VideoReferenceAiRequest(
    [property: Required, MaxLength(30)] string Goal,
    [property: Range(1, 20)] int Quantity,
    [property: Range(1, 20000)] int SourceWidth,
    [property: Range(1, 20000)] int SourceHeight,
    [property: MinLength(1), MaxLength(16)] IReadOnlyList<VideoReferenceAiCandidate> Candidates);

public sealed record VideoReferenceAiRank(string Id, int Rank, string Reason);

public sealed record VideoReferenceAiResponse(
    bool Ran,
    IReadOnlyList<VideoReferenceAiRank> Rankings,
    VideoReferenceCropDto? SuggestedCrop,
    string CropConfidence,
    string? Detail = null);

public sealed record ReferenceCropPresetRequest(
    [property: Required, MinLength(1), MaxLength(120)] string Name,
    [property: Range(1, 20000)] int SourceWidth,
    [property: Range(1, 20000)] int SourceHeight,
    VideoReferenceCropDto Crop,
    [property: Required, MaxLength(30)] string Kind);

public sealed record ReferenceCropPresetResponse(
    Guid Id, Guid BrandId, string Name, int SourceWidth, int SourceHeight,
    VideoReferenceCropDto Crop, string Kind, DateTimeOffset UpdatedAt);
