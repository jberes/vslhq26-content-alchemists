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
/// <summary>
/// At least one of <c>Url</c> and <c>Notes</c> must be supplied. Notes is whatever the user
/// pastes — a brand guide, a voice doc, an email from marketing — and is treated as more
/// authoritative than the website, because it was written on purpose.
/// </summary>
public sealed record BrandLookupRequest(
    [property: MaxLength(2000)] string? Url = null,
    [property: MaxLength(60000)] string? Notes = null);

public sealed record BrandLookupResponse(
    string Name, BrandStyleCard StyleCard, string SourceUrl, IReadOnlyList<string> Notes);

public sealed record BrandProfileUpsertRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    BrandStyleCard? StyleCard);

/// <summary>StyleCard is null when the stored JSON predates the schema and does not
/// parse; RawStyleCardJson always carries what is stored, so nothing is ever a 500.</summary>
public sealed record BrandProfileDetailResponse(
    Guid Id, string Name, BrandStyleCard? StyleCard, string? RawStyleCardJson,
    DateTimeOffset UpdatedAt, bool IsOwner = true);

/// <summary>The light shape carried on campaign payloads and pickers.</summary>
/// <summary>HasKnowledge: the brand carries a RAG endpoint, skills or MCP servers (ADR-056), so the Tech Edit can ground on it.</summary>
public sealed record BrandSummaryResponse(Guid Id, string Name, bool HasKnowledge = false);

public sealed record BrandCollaboratorRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email);

public sealed record BrandCollaboratorResponse(
    Guid Id,
    Guid UserId,
    string Email,
    string DisplayName,
    DateTimeOffset GrantedAt);

public sealed record BrandAssetLinkRequest(
    [property: Required] Guid AssetId,
    [property: Required, MaxLength(20)] string Kind,
    [property: MaxLength(200)] string? Label);

/// <summary>Rename only — the label IS the prompt text, so it must be editable in place
/// without re-uploading the file it describes.</summary>
public sealed record BrandAssetLabelRequest([property: MaxLength(200)] string? Label);

/// <summary>Reclassifies an existing kit image without re-uploading its bytes.</summary>
public sealed record BrandAssetKindRequest(
    [property: Required, MinLength(1), MaxLength(20)] string Kind);

public sealed record BrandAssetResponse(
    Guid Id, Guid BrandId, Guid AssetId, string Kind, string? Label,
    string FileName, string ContentType, DateTimeOffset CreatedAt);

public sealed record BrandTemplateRequest(
    [property: Required, MaxLength(50)] string Kind,
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: Required, MinLength(1), MaxLength(20000)] string SteeringPrompt,
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

// ---- Brand knowledge (ADR-056) -----------------------------------------------

public sealed record BrandKnowledgeSourceRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: Required, Url, MaxLength(2000)] string BaseUrl,
    [property: MaxLength(200)] string QueryPath = "/query",
    [property: MaxLength(100)] string QueryField = "query",
    /// <summary>Bearer token. Null leaves the stored one; empty string clears it.</summary>
    [property: MaxLength(4000)] string? Token = null,
    bool Enabled = true);

public sealed record BrandKnowledgeSourceResponse(
    Guid Id, Guid BrandId, string Name, string BaseUrl, string QueryPath, string QueryField,
    bool HasToken, bool Enabled, DateTimeOffset UpdatedAt);

public sealed record BrandSkillRequest(
    [property: Required, MinLength(1), MaxLength(200)] string Name,
    [property: Required, MinLength(1), MaxLength(300)] string FileName,
    [property: Required, MinLength(1), MaxLength(65536)] string Content,
    /// <summary>Comma list of generator kinds this skill applies to; null = every kind.</summary>
    [property: MaxLength(400)] string? AppliesTo = null,
    bool Enabled = true);

public sealed record BrandSkillResponse(
    Guid Id, Guid BrandId, string Name, string FileName, string Content, string? AppliesTo,
    bool Enabled, DateTimeOffset UpdatedAt);

public sealed record BrandMcpServerRequest(
    [property: Required, MinLength(1), MaxLength(100), RegularExpression("^[A-Za-z0-9_-]+$")] string Name,
    [property: Required, Url, MaxLength(2000)] string Url,
    /// <summary>Full Authorization header value (e.g. "Bearer …"). Null leaves the stored one; empty clears.</summary>
    [property: MaxLength(4000)] string? Authorization = null,
    [property: MaxLength(50)] IReadOnlyList<string>? AllowedTools = null,
    bool Enabled = true);

public sealed record BrandMcpServerResponse(
    Guid Id, Guid BrandId, string Name, string Url, bool HasAuthorization,
    IReadOnlyList<string>? AllowedTools, bool Enabled, DateTimeOffset UpdatedAt);

/// <summary>One brand's whole knowledge configuration, as the editor's Knowledge tab loads it.</summary>
public sealed record BrandKnowledgeResponse(
    IReadOnlyList<BrandKnowledgeSourceResponse> Sources,
    IReadOnlyList<BrandSkillResponse> Skills,
    IReadOnlyList<BrandMcpServerResponse> McpServers);
