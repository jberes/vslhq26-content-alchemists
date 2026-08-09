using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Ai;

/// <summary>
/// One timed segment of a source transcript. Segment ids are the provenance
/// anchors (G5): every generated claim cites the segments it came from.
/// </summary>
public sealed record TranscriptSegment(
    string Id, double StartSeconds, double EndSeconds, string? Speaker, string Text,
    /// <summary>Original recording/file label in a combined multi-source transcript.</summary>
    string? SourceLabel = null);

/// <summary>Stored as the ContentJson of an artifact with Kind = "transcript".</summary>
public sealed record TranscriptContent(string Source, IReadOnlyList<TranscriptSegment> Segments);

public sealed record TranscriptIngestRequest(
    [property: Required, MinLength(20), MaxLength(400_000)] string Text,
    [property: MaxLength(200)] string? Source,
    /// <summary>
    /// Real timed segments, when the client transcribed locally (desktop Whisper — roadmap
    /// E7.3). When present these are used verbatim (ids normalised server-side); the plain
    /// Text is then only a validation/audit copy. Without this, locally transcribed media
    /// would round-trip through sentence-splitting and lose its genuine timestamps — and
    /// timestamps are the provenance backbone.
    /// </summary>
    IReadOnlyList<TranscriptSegment>? Segments = null);

public sealed record GenerateRequest(
    [property: Required] Guid TranscriptArtifactId,
    [property: MaxLength(4000)] string? Brief,
    /// <summary>Subset of generator kinds to run; null/empty = the full fan-out.</summary>
    string[]? Kinds,
    /// <summary>
    /// How many of each requested kind to print. Kinds is a SET, not a bag — asking for
    /// "social-linkedin" twice in the array still generates it once — so "three more LinkedIn
    /// posts" is expressed here. Capped so a slip of the keyboard cannot start 500 generations.
    /// </summary>
    [property: Range(1, GenerateRequest.MaxCopies)] int Count = 1,
    /// <summary>Optional blog/pillar that owns the generated derivative.</summary>
    Guid? ParentArtifactId = null,
    /// <summary>Optional placeholder artifact replaced in place by generation.</summary>
    Guid? ReplaceArtifactId = null)
{
    public const int MaxCopies = 5;
}

public sealed record GenerationResult(
    string Kind,
    bool Success,
    Guid? ArtifactId,
    string? Error,
    IReadOnlyList<string> ValidationWarnings,
    long DurationMs);

public sealed record AiStatusResponse(
    string CredentialSource,
    bool EndpointConfigured,
    IReadOnlyDictionary<string, string> Models,
    bool SpeechConfigured,
    string? ProbeResult,
    /// <summary>Per-image-provider readiness (B9.5) so a client can disable a model with a reason.</summary>
    IReadOnlyList<ImageProviderReadiness> ImageProviders,
    /// <summary>Per-text-provider readiness (ADR-020) — the Tech Edit button reads this.</summary>
    IReadOnlyList<TextProviderReadiness>? TextProviders = null,
    /// <summary>Whether a knowledge-base gateway is configured AND has a stored token.</summary>
    bool KnowledgeBaseReady = false);

public sealed record ImageProviderReadiness(
    string Name, bool Ready, string? Reason, bool SupportsReferenceImages = false);

public sealed record TextProviderReadiness(string Name, bool Ready, string? Reason);

/// <summary>Something the Scout suggests making — or explicitly suggests NOT making.</summary>
public sealed record ScoutSuggestion(
    string Kind,
    string Title,
    string Angle,
    IReadOnlyList<string> TargetKeywords,
    string Rationale,
    /// <summary>new · refresh · covered. "covered" is a real answer, not a failure.</summary>
    string Coverage,
    /// <summary>Real URLs backing a "covered" or "refresh" verdict.</summary>
    IReadOnlyList<ScoutEvidence> Evidence);

public sealed record ScoutEvidence(string Title, string Url);

/// <summary>One tool call the Scout made — the trace that keeps the agent inspectable.</summary>
public sealed record ScoutStep(string Tool, string Query, string Result);

public sealed record ScoutResult(
    bool Success,
    string? Error,
    IReadOnlyList<ScoutSuggestion> Suggestions,
    IReadOnlyList<ScoutStep> Trace,
    long DurationMs);

public sealed record ScoutRequest(
    [property: MaxLength(500)] string? Focus,
    [property: Range(1, 10)] int Count = 5);

/// <summary>Body of a Tech Edit request (ADR-020).</summary>
public sealed record TechEditRequest(
    [property: MaxLength(4000)] string? Steering,
    /// <summary>Consult the customer knowledge base. Ignored when none is configured.</summary>
    bool UseKnowledgeBase = false);

/// <summary>
/// Outcome of a second pass. <see cref="Changes"/> is the model's own account of what it
/// altered — surfaced in the producer log so the edit is reviewable rather than mysterious.
/// </summary>
public sealed record TechEditResult(
    bool Success,
    string? Error,
    Guid ArtifactId,
    long Version,
    string Provider,
    bool KnowledgeBaseUsed,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings,
    long DurationMs);

/// <summary>
/// The brief the model reads off a transcript, so step 3 of the run flow is a review rather
/// than a form. Every field is nullable: a thin transcript should leave a box empty rather
/// than fill it with invention.
/// </summary>
public sealed record BriefSuggestionResponse(
    string? Title, string? Audience, string? BrandVoice, string? Angle,
    string? Summary, IReadOnlyList<string> KeyPoints);

/// <summary>
/// Research context inferred before SEO/AEO analysis. This intentionally contains no title,
/// angle or content copy: those are downstream of report approval.
/// </summary>
public sealed record ResearchContextSuggestionResponse(string? Audience);
