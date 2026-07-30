using Castmill.Core.Ai;
using Whisper.net;

namespace Castmill.Media;

/// <summary>
/// Downloads and caches Whisper ggml models (roadmap E7.3's "model download manager").
/// Models live in the app-data directory the host supplies, survive restarts, and download
/// with real percent progress — never an indeterminate spinner (ADR-F13).
/// </summary>
public sealed class WhisperModelManager(string modelsDirectory, HttpClient? http = null)
{
    /// <summary>ggml checkpoints published by the whisper.cpp project.</summary>
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    private readonly HttpClient _http = http ?? new HttpClient();

    public string PathFor(string model) => Path.Combine(modelsDirectory, $"ggml-{model}.bin");

    public bool IsCached(string model) => File.Exists(PathFor(model));

    public async Task<string> EnsureAsync(
        string model, IProgress<MediaProgress>? progress, CancellationToken ct = default)
    {
        var path = PathFor(model);
        if (File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(modelsDirectory);
        var partial = path + ".partial";

        try
        {
            using var response = await _http.GetAsync(
                $"{BaseUrl}/ggml-{model}.bin", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;

                    if (total is > 0)
                    {
                        progress?.Report(new MediaProgress(
                            $"downloading whisper {model} model",
                            written * 100.0 / total.Value,
                            $"{written / 1_048_576} of {total.Value / 1_048_576} MB"));
                    }
                }
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}

/// <summary>
/// Local transcription over Whisper.net (whisper.cpp). Input is any file ffmpeg can read;
/// output is the same timed-segment shape the server produces, so a locally transcribed
/// campaign is indistinguishable from a cloud-transcribed one downstream.
/// </summary>
public static class WhisperTranscriber
{
    public static async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string mediaPath,
        string modelPath,
        IProgress<MediaProgress>? progress,
        CancellationToken ct = default)
    {
        var wavPath = await AudioExtractor.ExtractAsync(mediaPath, progress, ct);

        try
        {
            var duration = await Ffmpeg.ProbeDurationAsync(wavPath, ct);

            using var factory = WhisperFactory.FromPath(modelPath);
            await using var processor = factory.CreateBuilder()
                .WithLanguage("auto")
                .Build();

            var segments = new List<TranscriptSegment>();

            await using var wav = File.OpenRead(wavPath);
            await foreach (var result in processor.ProcessAsync(wav, ct))
            {
                var text = result.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                segments.Add(new TranscriptSegment(
                    $"s{segments.Count + 1:00}",
                    result.Start.TotalSeconds,
                    result.End.TotalSeconds,
                    Speaker: null, // whisper.cpp does not diarize; the cloud Speech path does
                    text));

                if (duration.TotalSeconds > 0)
                {
                    progress?.Report(new MediaProgress(
                        "transcribing",
                        Math.Clamp(result.End.TotalSeconds / duration.TotalSeconds * 100, 0, 100),
                        $"{segments.Count} segments"));
                }
            }

            progress?.Report(new MediaProgress("transcribing", 100, $"{segments.Count} segments"));
            return segments;
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
