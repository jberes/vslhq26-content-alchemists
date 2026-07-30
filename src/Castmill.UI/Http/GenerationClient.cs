using Castmill.Core.Ai;

namespace Castmill.UI.Http;

/// <summary>What the transcript-ingest endpoint returns.</summary>
public sealed record IngestResult(Guid TranscriptArtifactId, int SegmentCount)
{
    /// <summary>Friendlier name for the narrated log line.</summary>
    public int Segments => SegmentCount;
}

/// <summary>One generator's outcome inside a run — the shape the orchestrator records.</summary>
public sealed record RunItem(
    string Kind,
    bool Success,
    Guid? ArtifactId,
    string? Error,
    IReadOnlyList<string>? ValidationWarnings,
    long DurationMs);

/// <summary>
/// Run progress, as the Press Run polls it (backend B9.8). Items appear in completion
/// order, which is what drives the reveal — never a client-side timer (ADR-F13).
/// </summary>
public sealed record RunProgress(
    Guid Id,
    Guid CampaignId,
    string Status,
    int TotalKinds,
    int Completed,
    IReadOnlyList<RunItem> Items,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsComplete => string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The whole-fan-out response, returned when every generator has finished.</summary>
public sealed record RunFinished(Guid RunId, int Succeeded, int Failed, IReadOnlyList<RunItem> Results);

/// <summary>Typed client for <c>/api/v1/ai/*</c> — ingest, generation and run progress.</summary>
public sealed class GenerationClient(ApiClient api)
{
    public Task<IngestResult> IngestTranscriptAsync(
        Guid campaignId, string text, string source = "pasted",
        IReadOnlyList<TranscriptSegment>? segments = null, CancellationToken ct = default) =>
        api.PostAsync<object, IngestResult>(
            $"api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text, source, segments },
            anonymous: false,
            ct);

    /// <summary>
    /// Starts the fan-out. v1 generation is request/response (backend ADR-006): this call
    /// returns when every generator has finished. The Press Run therefore does not await it
    /// directly — <see cref="PressRunService"/> holds the task and polls the latest run.
    /// </summary>
    public Task<RunFinished> GenerateAsync(
        Guid campaignId, Guid transcriptArtifactId, string? brief, string[] kinds,
        CancellationToken ct = default) =>
        api.PostAsync<object, RunFinished>(
            $"api/v1/ai/campaigns/{campaignId}/generate",
            new { transcriptArtifactId, brief, kinds },
            anonymous: false,
            ct);

    /// <summary>Regenerates one kind (Focus mode's Producer rail). Note backend 🔶 5.7: this
    /// currently inserts a new artifact row rather than revising in place.</summary>
    public Task<RunItem> GenerateOneAsync(
        Guid campaignId, string kind, Guid transcriptArtifactId, string? brief,
        CancellationToken ct = default) =>
        api.PostAsync<object, RunItem>(
            $"api/v1/ai/campaigns/{campaignId}/generate/{Uri.EscapeDataString(kind)}",
            new { transcriptArtifactId, brief },
            anonymous: false,
            ct);

    public Task<RunProgress> GetRunAsync(Guid runId, CancellationToken ct = default) =>
        api.GetAsync<RunProgress>($"api/v1/ai/runs/{runId}", ct);

    /// <summary>The campaign's most recent run — how the Press Run finds an in-flight run,
    /// since the generate POST cannot reveal its run id until it has already finished.</summary>
    public Task<RunProgress> GetLatestRunAsync(Guid campaignId, CancellationToken ct = default) =>
        api.GetAsync<RunProgress>($"api/v1/ai/campaigns/{campaignId}/runs/latest", ct);
}
