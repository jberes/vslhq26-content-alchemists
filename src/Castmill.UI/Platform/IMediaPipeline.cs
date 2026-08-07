using Castmill.Core.Ai;

namespace Castmill.UI.Platform;

/// <summary>A media file picked through the shell's native picker, with a real path.</summary>
public sealed record PickedMedia(string Path, string FileName, long SizeBytes);

/// <summary>Engine progress surfaced to the narrated logs (ADR-F13: always determinate).</summary>
public sealed record PipelineProgress(string Stage, double? Percent, string? Detail = null);

public sealed record LocalTranscription(IReadOnlyList<TranscriptSegment> Segments)
{
    public string JoinedText => string.Join(" ", Segments.Select(s => s.Text));
}

public sealed record ClipExportOptions(
    double StartSeconds,
    double EndSeconds,
    bool ReEncode,
    /// <summary>Reframe to a platform-exact vertical 1080×1920.</summary>
    bool CropVertical,
    bool BurnCaptions,
    /// <summary>Publishing copy written beside the clip as a .txt (upload is still manual).</summary>
    ClipPublishCopy? PublishCopy = null,
    /// <summary>File stem, so a batch reads as clip-01, clip-02… instead of timestamps.</summary>
    string? OutputName = null);

public sealed record ClipPublishCopy(
    string? Title, string? Description, IReadOnlyList<string>? Hashtags, string? Hook);

/// <summary>
/// The media seam between the shells (Roadmap §2.2). Desktop implements it with the local
/// engine (ffmpeg + Whisper); web implements it as capability-flagged OFF with a stated
/// reason, so no surface ever shows a dead control (G3) — web media goes through the cloud
/// endpoints instead.
/// </summary>
public interface IMediaPipeline
{
    /// <summary>True when this shell can extract, transcribe and cut media on-device.</summary>
    bool CanProcessLocally { get; }

    /// <summary>Why not, when it can't — shown verbatim in the UI (G3).</summary>
    string? UnavailableReason { get; }

    /// <summary>Native file picker for audio/video. Null when the user cancels.</summary>
    Task<PickedMedia?> PickMediaAsync();

    /// <summary>MP3/MP4 (anything ffmpeg reads) → timed segments, entirely on-device.</summary>
    Task<LocalTranscription> TranscribeAsync(
        PickedMedia media, IProgress<PipelineProgress> progress, CancellationToken ct = default);

    /// <summary>Cuts a clip from local source media; returns the output file path.</summary>
    Task<string> ExportClipAsync(
        PickedMedia source,
        ClipExportOptions options,
        IReadOnlyList<TranscriptSegment>? captionSegments,
        IProgress<PipelineProgress> progress,
        CancellationToken ct = default);

    /// <summary>The most recently picked media this session — clip export's default source.</summary>
    PickedMedia? LastPicked { get; }
}
