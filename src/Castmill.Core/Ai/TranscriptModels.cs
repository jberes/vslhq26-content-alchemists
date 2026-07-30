using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Ai;

/// <summary>
/// One timed segment of a source transcript. Segment ids are the provenance
/// anchors (G5): every generated claim cites the segments it came from.
/// </summary>
public sealed record TranscriptSegment(string Id, double StartSeconds, double EndSeconds, string? Speaker, string Text);

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
    string[]? Kinds);

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
    IReadOnlyList<ImageProviderReadiness> ImageProviders);

public sealed record ImageProviderReadiness(string Name, bool Ready, string? Reason);
