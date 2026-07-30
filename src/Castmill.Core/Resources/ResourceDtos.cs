using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Resources;

// ---- Campaigns -------------------------------------------------------------

public sealed record CampaignCreateRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: MaxLength(8000)] string? Brief);

public sealed record CampaignUpdateRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: MaxLength(8000)] string? Brief);

public sealed record CampaignResponse(
    Guid Id, Guid OwnerId, string Name, string? Brief,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// ---- Artifacts -------------------------------------------------------------

public sealed record ArtifactCreateRequest(
    [property: Required, MinLength(1), MaxLength(50)] string Kind,
    [property: Required, MinLength(1), MaxLength(300)] string Title,
    [property: Required] string ContentJson);

public sealed record ArtifactUpdateRequest(
    [property: Required, MinLength(1), MaxLength(300)] string Title,
    [property: Required] string ContentJson);

/// <summary>List-view projection (ADR-003): everything except the heavy content.</summary>
public sealed record ArtifactPreviewResponse(
    Guid Id, Guid CampaignId, string Kind, string Title, string Status, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    /// <summary>Transcript segment ids this artifact cites — the provenance threads' data.</summary>
    IReadOnlyList<string>? Citations = null);

public sealed record ArtifactResponse(
    Guid Id, Guid CampaignId, string Kind, string Title, string ContentJson, string Status,
    long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Status transitions are their own action, not part of a content save: "mark reviewed" and
/// "edit the copy" are different intents with different guards (roadmap E6.9's review gate).
/// </summary>
public sealed record ArtifactStatusRequest(
    [property: Required, MinLength(1), MaxLength(20)] string Status);

// ---- Artifact revisions (B9.7 / ADR-017) -----------------------------------

public sealed record ArtifactRevisionResponse(
    Guid Id, Guid ArtifactId, long Version, string Title, string Reason, DateTimeOffset CreatedAt);

public sealed record ArtifactRevisionDetailResponse(
    Guid Id, Guid ArtifactId, long Version, string Title, string Reason,
    string ContentJson, DateTimeOffset CreatedAt);

// ---- Image plan (B9.1 / ADR-012) -------------------------------------------

public sealed record ImageSlotResponse(
    Guid Id, Guid CampaignId, string Kind, int TargetWidth, int TargetHeight,
    string? Prompt, string? ModelAlias, string? SourceSegmentId,
    string? HeadlineText, bool SafeArea, string State, string? PublishedUrl, string? BaseImageUrl,
    DateTimeOffset UpdatedAt);

public sealed record ImageSlotPatchRequest(
    [property: MaxLength(4000)] string? Prompt,
    [property: MaxLength(100)] string? ModelAlias,
    [property: MaxLength(50)] string? SourceSegmentId,
    /// <summary>Composited after generation (ADR-013) — never sent to the model.</summary>
    [property: MaxLength(32)] string? HeadlineText,
    bool? SafeArea);

public sealed record GenerateVariantsRequest(
    [property: Range(1, 6)] int Variants = 2);

public sealed record ImageVariantResponse(int Index, string Url, string Model);

public sealed record PlaceVariantRequest(
    [property: Required, MaxLength(2000), Url] string Url,
    /// <summary>Blog artifact whose ![stub:kind]() marker gets replaced; optional.</summary>
    Guid? BlogArtifactId);

public sealed record CompositeHeadlineRequest(
    [property: Required] Guid CampaignId,
    [property: Required] Guid SlotId,
    [property: Required, MinLength(1), MaxLength(32)] string Headline,
    bool SafeArea = true);

// ---- Schedule mirror (B9.6 / ADR-016) --------------------------------------

public sealed record ScheduleEntryCreateRequest(
    [property: Required] Guid CampaignId,
    Guid? ArtifactId,
    [property: Required, MinLength(1), MaxLength(200)] string ChannelId,
    [property: Required, MinLength(1), MaxLength(65_000)] string Text,
    [property: Required] DateTimeOffset ScheduledAt,
    [property: MaxLength(2000)] string? MediaUrl,
    /// <summary>Push to the broker immediately; false keeps the entry local (Draft).</summary>
    bool PushToBroker = true);

public sealed record ScheduleEntryMoveRequest(
    [property: Required] DateTimeOffset ScheduledAt);

public sealed record ScheduleEntryResponse(
    Guid Id, Guid CampaignId, Guid? ArtifactId, string ChannelId, string? BrokerPostId,
    string Text, string? MediaUrl, DateTimeOffset ScheduledAt, string Status, string? Error,
    DateTimeOffset UpdatedAt);

// ---- Assets (metadata only until B3 wires blob SAS) ------------------------

public sealed record AssetCreateRequest(
    [property: Required, MinLength(1), MaxLength(400)] string FileName,
    [property: Required, MinLength(1), MaxLength(200)] string ContentType,
    [property: Range(0, 10_737_418_240)] long SizeBytes);

public sealed record AssetResponse(
    Guid Id, string FileName, string ContentType, long SizeBytes,
    string BlobPath, DateTimeOffset CreatedAt);

// ---- Brand profiles --------------------------------------------------------

public sealed record BrandProfileRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    string? StyleCardJson);

public sealed record BrandProfileResponse(
    Guid Id, string Name, string? StyleCardJson, DateTimeOffset UpdatedAt);

// ---- Settings (plaintext kinds only; encrypted kinds arrive in B3) ---------

public sealed record SettingWriteRequest(
    [property: Required, MaxLength(4000)] string Value);

public sealed record SettingResponse(string Key, string Value, DateTimeOffset UpdatedAt);
