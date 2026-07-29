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
    Guid Id, Guid CampaignId, string Kind, string Title, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ArtifactResponse(
    Guid Id, Guid CampaignId, string Kind, string Title, string ContentJson,
    long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

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
