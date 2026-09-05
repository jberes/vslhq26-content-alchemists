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

    private readonly LocalMediaServer _server = new();

    public bool CanProcessLocally => true;

    public string? UnavailableReason => null;

    public PickedMedia? LastPicked { get; private set; }

    public bool CanPlayLocalFiles => true;

    /// <summary>
    /// The WebView cannot load file:// media, so the desktop serves picked recordings from a
    /// loopback HTTP server with Range support (ADR-057). Only files registered here are
    /// reachable, each under an opaque token; nothing else on disk is exposed.
    /// </summary>
    public Task<string?> OpenLocalMediaAsync(string path) =>
        Task.FromResult(File.Exists(path) ? _server.Register(path) : null);

    public async Task<string?> FingerprintAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return await Task.Run(() => MediaFingerprint.Compute(path));
    }

    public Task<Stream?> OpenReadAsync(string path) =>
        Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true)
            : null);

    public async Task<PickedMedia?> PickMediaAsync()
    {
        // MAUI Essentials pickers MUST run on the main thread. Blazor Hybrid dispatches
        // component events off it, and calling the picker there doesn't fail — it
        // deadlocks the app with the dialog never shown.
        var result = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Pick a video or audio file",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.MacCatalyst] = ["public.movie", "public.audio"],
                [DevicePlatform.WinUI] = [".mp4", ".mov", ".m4v", ".mp3", ".m4a", ".wav", ".aac"],
            }),
        }));

        if (result is null)
        {
            return null;
        }

        var info = new FileInfo(result.FullPath);
        LastPicked = new PickedMedia(result.FullPath, result.FileName, info.Exists ? info.Length : 0);
        return LastPicked;
    }

    public async Task<IReadOnlyList<PickedMedia>> PickMediaFilesAsync()
    {
        var results = await MainThread.InvokeOnMainThreadAsync(() =>
            FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Pick one or more video or audio files",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.MacCatalyst] = ["public.movie", "public.audio"],
                    [DevicePlatform.WinUI] = [".mp4", ".mov", ".m4v", ".mp3", ".m4a", ".wav", ".aac"],
                }),
            }));
        var picked = results.Where(result => result is not null).Select(result =>
        {
            ArgumentNullException.ThrowIfNull(result);
            var info = new FileInfo(result.FullPath);
            return new PickedMedia(result.FullPath, result.FileName, info.Exists ? info.Length : 0);
        }).ToList();
        LastPicked = picked.FirstOrDefault();
        return picked;
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

        // One folder per source, under Downloads: a six-clip batch used to scatter six
        // files (plus their metadata sidecars) into a folder that already has hundreds.
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Castmill clips",
            Path.GetFileNameWithoutExtension(source.FileName));

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
                outputDirectory,
                options.PublishCopy is { } copy
                    ? new ClipMetadata(copy.Title, copy.Description, copy.Hashtags, copy.Hook)
                    : null,
                options.OutputName,
                options.Pillarbox ? ReframeMode.BlurredPillarbox : ReframeMode.Crop,
                options.HookOverlay,
                options.EndCard,
                options.CoverFrame),
            engineProgress,
            ct);
    }
}
