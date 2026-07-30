using Castmill.Core.Ai;
using Castmill.UI.Platform;

namespace Castmill.Web.Platform;

/// <summary>
/// Web has no local media engine — no process spawning, no whisper natives in WASM. The
/// capability is flagged off with a stated reason (G3: web never renders a dead control);
/// the web ingest path is the cloud transcription endpoint instead.
/// </summary>
internal sealed class WebMediaPipeline : IMediaPipeline
{
    public bool CanProcessLocally => false;

    public string? UnavailableReason =>
        "Local transcription and clip export run in the desktop app. On the web, audio "
        + "up to 25 MB transcribes in the cloud; larger media needs the desktop app until "
        + "the Azure Speech path is provisioned.";

    public PickedMedia? LastPicked => null;

    public Task<PickedMedia?> PickMediaAsync() => Task.FromResult<PickedMedia?>(null);

    public Task<LocalTranscription> TranscribeAsync(
        PickedMedia media, IProgress<PipelineProgress> progress, CancellationToken ct = default) =>
        throw new NotSupportedException(UnavailableReason);

    public Task<string> ExportClipAsync(
        PickedMedia source, ClipExportOptions options, IReadOnlyList<TranscriptSegment>? captionSegments,
        IProgress<PipelineProgress> progress, CancellationToken ct = default) =>
        throw new NotSupportedException(UnavailableReason);
}
