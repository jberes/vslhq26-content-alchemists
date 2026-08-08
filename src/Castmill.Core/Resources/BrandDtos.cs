using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Resources;

/// <summary>One named colour in a brand's scheme. Role is free-form ("primary",
/// "background", "highlight"); Hex is validated at the boundary.</summary>
public sealed record BrandColor(
    [property: Required, MaxLength(50)] string Role,
    [property: Required, RegularExpression("^#[0-9a-fA-F]{6}$")] string Hex);

/// <summary>
/// The typed style card serialized into <c>BrandProfile.StyleCardJson</c> (ADR-003:
/// typed JSON validated at the boundary, not normalized tables). Every field is optional —
/// a brand is useful with just a voice, or just a palette.
/// </summary>
public sealed record BrandStyleCard(
    [property: MaxLength(4000)] string? Voice = null,
    [property: MaxLength(2000)] string? Audience = null,
    [property: MaxLength(300)] string? Tagline = null,
    IReadOnlyList<BrandColor>? Colors = null,
    [property: MaxLength(200)] string? HeadingFont = null,
    [property: MaxLength(200)] string? BodyFont = null,
    [property: MaxLength(4000)] string? ImageStyle = null,
    IReadOnlyList<string>? BannedPhrases = null);

/// <summary>Draft a brand from its public website. The result is never saved automatically —
/// it populates the editor for the user to accept or change.</summary>
public sealed record BrandLookupRequest(
    [property: Required, MinLength(4), MaxLength(2000)] string Url);

public sealed record BrandLookupResponse(
    string Name, BrandStyleCard StyleCard, string SourceUrl, IReadOnlyList<string> Notes);

public sealed record BrandProfileUpsertRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    BrandStyleCard? StyleCard);

/// <summary>StyleCard is null when the stored JSON predates the schema and does not
/// parse; RawStyleCardJson always carries what is stored, so nothing is ever a 500.</summary>
public sealed record BrandProfileDetailResponse(
    Guid Id, string Name, BrandStyleCard? StyleCard, string? RawStyleCardJson, DateTimeOffset UpdatedAt);

/// <summary>The light shape carried on campaign payloads and pickers.</summary>
public sealed record BrandSummaryResponse(Guid Id, string Name);

public sealed record BrandAssetLinkRequest(
    [property: Required] Guid AssetId,
    [property: Required, MaxLength(20)] string Kind,
    [property: MaxLength(200)] string? Label);

public sealed record BrandAssetResponse(
    Guid Id, Guid BrandId, Guid AssetId, string Kind, string? Label,
    string FileName, string ContentType, DateTimeOffset CreatedAt);

public sealed record BrandTemplateRequest(
    [property: Required, MaxLength(50)] string Kind,
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: Required, MinLength(1), MaxLength(4000)] string SteeringPrompt,
    bool IsDefault = false);

public sealed record BrandTemplateResponse(
    Guid Id, Guid BrandId, string Kind, string Name, string SteeringPrompt,
    bool IsDefault, DateTimeOffset UpdatedAt);

/// <summary>A context link on a campaign — home page, GitHub pages, docs — that informs
/// generation. Stored as a JSON array on the campaign (max 10, validated).</summary>
public sealed record CampaignLink(
    [property: Required, MaxLength(100)] string Label,
    [property: Required, MaxLength(2000), Url] string Url,
    [property: MaxLength(500)] string? Note = null);
