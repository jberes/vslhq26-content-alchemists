using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Resources;

/// <summary>One named colour in a brand's scheme. Role stays free text — a brand may have a
/// "wash" or a "chart series 3" — but <see cref="BrandColorRoles"/> is the offered set, and the
/// importer maps onto it so three colours do not all arrive called "accent" (ADR-062).
/// <see cref="Token"/> is the brand's own CSS variable name when its guide names one.</summary>
public sealed record BrandColor(
    [property: Required, MaxLength(50)] string Role,
    [property: Required, RegularExpression("^#[0-9a-fA-F]{6}$")] string Hex,
    [property: MaxLength(100)] string? Token = null);

/// <summary>The roles the editor offers and the importer maps onto. Free text still allowed.</summary>
public static class BrandColorRoles
{
    public static readonly IReadOnlyList<string> Known =
        ["primary", "secondary", "accent", "cta", "neutral", "background", "surface", "text", "border", "success", "warning", "danger"];

    /// <summary>
    /// Maps a guide's own wording onto a known role: "Primary Blue (CTA)" → "cta",
    /// "Border Gray" → "border", "Inverse Text" → "text". Anything unrecognised is kept as
    /// written, trimmed — a brand's vocabulary is not ours to overwrite.
    /// </summary>
    public static string Normalize(string? role)
    {
        var text = role?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "accent";
        }
        var lower = text.ToLowerInvariant();
        // Most specific first: "primary action" is a CTA, not the primary brand colour.
        if (Word(lower, "cta") || Word(lower, "action")) return "cta";
        if (Word(lower, "background") || Word(lower, "bg")) return "background";
        if (Word(lower, "surface")) return "surface";
        if (Word(lower, "border") || Word(lower, "rule")) return "border";
        // Whole words only: "pink" contains "ink", which quietly turned every accent pink into
        // a text colour the first time this ran.
        if (Word(lower, "text") || Word(lower, "ink") || Word(lower, "foreground")) return "text";
        if (Word(lower, "neutral") || Word(lower, "gray") || Word(lower, "grey")) return "neutral";
        if (Word(lower, "secondary")) return "secondary";
        if (Word(lower, "primary")) return "primary";
        if (Word(lower, "accent") || Word(lower, "highlight")) return "accent";
        if (Word(lower, "success")) return "success";
        if (Word(lower, "warn") || Word(lower, "warning")) return "warning";
        if (Word(lower, "danger") || Word(lower, "error")) return "danger";
        return text;
    }

    private static bool Word(string text, string word) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b");

    /// <summary>
    /// One row per colour: the same hex twice is one colour with two names, and a role that
    /// repeats is numbered rather than dropped — a brand really can have three accents.
    /// </summary>
    public static IReadOnlyList<BrandColor> Dedupe(IEnumerable<BrandColor> colors)
    {
        var byHex = new Dictionary<string, BrandColor>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var color in colors)
        {
            var hex = color.Hex.Trim().ToUpperInvariant();
            if (byHex.ContainsKey(hex))
            {
                continue;
            }
            byHex[hex] = color with { Role = Normalize(color.Role), Hex = hex };
            order.Add(hex);
        }

        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BrandColor>(order.Count);
        foreach (var hex in order)
        {
            var color = byHex[hex];
            var seen = used.GetValueOrDefault(color.Role);
            used[color.Role] = seen + 1;
            result.Add(seen == 0 ? color : color with { Role = $"{color.Role} {seen + 1}" });
        }
        return result;
    }
}

/// <summary>A competitor and the situation it turns up in — enough for comparison content.</summary>
public sealed record BrandCompetitor(
    [property: Required, MaxLength(200)] string Name,
    [property: MaxLength(1000)] string? WhenItComesUp = null);

/// <summary>Who the content is written for, in the detail a writer can act on.</summary>
public sealed record BrandPersona(
    [property: Required, MaxLength(200)] string Title,
    [property: MaxLength(1000)] string? Role = null,
    IReadOnlyList<string>? Goals = null,
    IReadOnlyList<string>? PainPoints = null,
    IReadOnlyList<string>? DecisionCriteria = null);

/// <summary>A gradient the palette cannot express as a single hex.</summary>
public sealed record BrandGradient(
    [property: Required, MaxLength(50)] string Role,
    [property: Required, MaxLength(500)] string Css);

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
    IReadOnlyList<string>? BannedPhrases = null,

    // ---- Market and messaging (ADR-062). What makes a piece this brand's rather than
    // ---- anyone's: the claim it makes, the evidence for it, and who it is aimed at.
    /// <summary>What the product is and who it is for, in the brand's own words.</summary>
    [property: MaxLength(4000)] string? Positioning = null,
    /// <summary>The handful of themes every piece should ladder up to.</summary>
    IReadOnlyList<string>? MessagingPillars = null,
    /// <summary>Claims this brand may make that its competitors cannot.</summary>
    IReadOnlyList<string>? Differentiators = null,
    /// <summary>Evidence for those claims — outcomes, benchmarks, named wins.</summary>
    IReadOnlyList<string>? ProofPoints = null,
    IReadOnlyList<BrandCompetitor>? Competitors = null,
    IReadOnlyList<BrandPersona>? Personas = null,
    /// <summary>Concrete scenarios the product is bought for.</summary>
    IReadOnlyList<string>? UseCases = null,
    /// <summary>Guardrail: claims and conflations this brand must never make. Sibling products
    /// belong here — confusing them is the failure mode a generator falls into most easily.</summary>
    IReadOnlyList<string>? DoNotClaim = null,

    // ---- Visual rules (ADR-062). A palette says which colours exist; these say how to use them.
    /// <summary>Proportions and placement rules, e.g. "70-85% neutral; CTA must be blue".</summary>
    [property: MaxLength(2000)] string? ColorUsage = null,
    IReadOnlyList<BrandGradient>? Gradients = null,
    /// <summary>The section order a page follows, e.g. "Hero -> Features -> CTA".</summary>
    [property: MaxLength(1000)] string? LayoutPattern = null,
    /// <summary>Visual things to avoid — the negative half of the image brief.</summary>
    IReadOnlyList<string>? VisualDontList = null,

    // ---- Review (ADR-062).
    /// <summary>What a finished piece is checked against before it ships.</summary>
    IReadOnlyList<string>? QaChecklist = null,
    /// <summary>How marketing surfaces differ from in-product surfaces.</summary>
    [property: MaxLength(1000)] string? MarketingMode = null,
    [property: MaxLength(1000)] string? ProductMode = null);

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
    bool Enabled = true,
    /// <summary>Product this brand asks about on a shared gateway, e.g. "reveal" (ADR-061).</summary>
    [property: MaxLength(100)] string? ProductType = null,
    [property: MaxLength(100)] string ProductField = "productType");

public sealed record BrandKnowledgeSourceResponse(
    Guid Id, Guid BrandId, string Name, string BaseUrl, string QueryPath, string QueryField,
    bool HasToken, bool Enabled, DateTimeOffset UpdatedAt,
    string? ProductType = null, string ProductField = "productType");

/// <summary>
/// Reuse another brand's gateway settings (ADR-061). The copy happens server-side because the
/// bearer token is write-only — the client never sees it and so could not copy it. Only the
/// product differs, which is why it is the one field this request supplies.
/// </summary>
public sealed record BrandKnowledgeSourceCopyRequest(
    [property: Required] Guid SourceBrandId,
    [property: Required] Guid SourceId,
    /// <summary>Product for the NEW brand, e.g. "reveal". Null copies the source's.</summary>
    [property: MaxLength(100)] string? ProductType = null,
    /// <summary>Name for the copy; null keeps the source's name.</summary>
    [property: MaxLength(200)] string? Name = null);

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
