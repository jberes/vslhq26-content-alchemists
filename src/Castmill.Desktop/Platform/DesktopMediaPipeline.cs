using Castmill.Core.Ai;
using Castmill.Media;
using Castmill.UI.Platform;

namespace Castmill.Desktop.Platform;

/// <summary>
/// The desktop media pipeline (roadmap E7.2/E7.3/E7.5): MAUI file picker + the
/// Castmill.Media engine — ffmpeg extraction, Whisper.net transcription with a cached
/// model, ffmpeg clip export. Everything runs on-device; nothing touches the network
/// except the one-time model download.
/// </summary>
internal sealed class DesktopMediaPipeline : IMediaPipeline
{
    /// <summary>base = the multilingual ~142 MB checkpoint: solid accuracy at laptop speed.
    /// A model picker is a settings story; the manager already takes any ggml name.</summary>
    private const string DefaultModel = "base";

    private readonly WhisperModelManager _models = new(
        Path.Combine(FileSystem.AppDataDirectory, "whisper"));

    public bool CanProcessLocally => true;

    public string? UnavailableReason => null;

    public PickedMedia? LastPicked { get; private set; }

    public async Task<PickedMedia?> PickMediaAsync()
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Pick a video or audio file",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.MacCatalyst] = ["public.movie", "public.audio"],
                [DevicePlatform.WinUI] = [".mp4", ".mov", ".m4v", ".mp3", ".m4a", ".wav", ".aac"],
            }),
        });

        if (result is null)
        {
            return null;
        }

        var info = new FileInfo(result.FullPath);
        LastPicked = new PickedMedia(result.FullPath, result.FileName, info.Exists ? info.Length : 0);
        return LastPicked;
    }

    public async Task<LocalTranscription> TranscribeAsync(
        PickedMedia media, IProgress<PipelineProgress> progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);

        var engineProgress = new Progress<MediaProgress>(p =>
            progress.Report(new PipelineProgress(p.Stage, p.Percent, p.Detail)));

        var modelPath = await _models.EnsureAsync(DefaultModel, engineProgress, ct);
        var segments = await WhisperTranscriber.TranscribeAsync(media.Path, modelPath, engineProgress, ct);

        return new LocalTranscription(segments);
    }

    public Task<string> ExportClipAsync(
        PickedMedia source,
        ClipExportOptions options,
        IReadOnlyList<TranscriptSegment>? captionSegments,
        IProgress<PipelineProgress> progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        var engineProgress = new Progress<MediaProgress>(p =>
            progress.Report(new PipelineProgress(p.Stage, p.Percent, p.Detail)));

        return ClipExporter.ExportAsync(
            new ClipExportRequest(
                source.Path,
                options.StartSeconds,
                options.EndSeconds,
                options.ReEncode,
                options.CropVertical,
                options.BurnCaptions ? captionSegments : null,
                downloads),
            engineProgress,
            ct);
    }
}
