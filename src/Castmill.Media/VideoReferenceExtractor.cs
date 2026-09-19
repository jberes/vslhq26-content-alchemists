using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Castmill.Media;

public sealed record VideoMetadata(
    double DurationSeconds,
    int Width,
    int Height,
    double FrameRate,
    int Rotation = 0);

public sealed record PixelCrop(int X, int Y, int Width, int Height, string Method, double Confidence)
{
    public static PixelCrop Full(VideoMetadata metadata) =>
        new(0, 0, metadata.Width, metadata.Height, "none", 1);
}

public sealed record ExtractedFrame(
    string Id,
    double TimestampSeconds,
    long FrameNumber,
    byte[] PreviewJpeg,
    ulong PerceptualHash,
    double QualityScore,
    string? Warning = null,
    string? AiSummary = null);

public sealed record VideoReferenceAnalysis(
    VideoMetadata Metadata,
    IReadOnlyList<ExtractedFrame> Frames,
    PixelCrop SuggestedCrop,
    string CropConfidence,
    bool UsedSceneChanges);

/// <summary>
/// Deterministic video still extraction. FFmpeg decodes source pixels; the C# layer only
/// scores small grayscale proxies, computes dHash similarity, and chooses timestamps.
/// Final output is PNG with an integer crop and no resize or generative operation.
/// </summary>
public static partial class VideoReferenceExtractor
{
    private const int SampleWidth = 64;
    private const int SampleHeight = 36;
    private const int MaxCandidates = 50;

    [GeneratedRegex(@"pts_time:(?<time>\d+(?:\.\d+)?)")]
    private static partial Regex SceneTimeRegex();

    public static async Task<VideoMetadata> ProbeAsync(string path, CancellationToken ct = default)
    {
        EnsureVideo(path);
        var (_, stdout, _) = await Ffmpeg.RunCaptureAsync(
            Ffmpeg.RequireProbe(),
            ["-v", "error", "-select_streams", "v:0",
             "-show_entries", "stream=width,height,avg_frame_rate:stream_tags=rotate:format=duration",
             "-of", "json", path], ct);
        using var json = JsonDocument.Parse(stdout);
        var stream = json.RootElement.GetProperty("streams")[0];
        var format = json.RootElement.GetProperty("format");
        var duration = ReadDouble(format, "duration");
        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();
        var fps = ParseRate(stream.TryGetProperty("avg_frame_rate", out var rate)
            ? rate.GetString()
            : null);
        var rotation = stream.TryGetProperty("tags", out var tags)
            && tags.TryGetProperty("rotate", out var rotate)
            && int.TryParse(rotate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        if (rotation is 90 or -90 or 270)
        {
            (width, height) = (height, width);
        }
        if (duration <= 0 || width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The selected file has no readable video stream.");
        }
        return new VideoMetadata(duration, width, height, fps <= 0 ? 30 : fps, rotation);
    }

    public static async Task<VideoReferenceAnalysis> AnalyzeAsync(
        string path,
        int quantity,
        string diversity,
        IProgress<MediaProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (quantity is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Choose between 1 and 30 frames.");
        }
        var metadata = await ProbeAsync(path, ct);
        progress?.Report(new MediaProgress("probing video", 5,
            $"{metadata.Width}×{metadata.Height} · {metadata.DurationSeconds:0.0}s"));

        // Fifteen proxy samples are enough to produce a varied five-frame set while keeping
        // a five-minute 4K screen recording inside the interactive latency budget. Larger
        // requested sets scale the pool up, capped to prevent seek storms.
        var regularCount = Math.Clamp(quantity * 3, 15, MaxCandidates);
        var timestamps = RegularTimestamps(metadata.DurationSeconds, regularCount).ToList();
        var useScenes = !diversity.Equals("even", StringComparison.OrdinalIgnoreCase);
        if (useScenes)
        {
            foreach (var scene in await DetectSceneChangesAsync(path, metadata.DurationSeconds, ct))
            {
                if (scene > .15 && scene < metadata.DurationSeconds - .15)
                {
                    timestamps.Add(scene);
                }
            }
        }
        timestamps = timestamps.DistinctBy(value => Math.Round(value, 2))
            .OrderBy(value => value).Take(MaxCandidates).ToList();

        var scored = new List<ProxyScore>(timestamps.Count);
        for (var index = 0; index < timestamps.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var gray = await ExtractGrayAsync(path, timestamps[index], SampleWidth, SampleHeight, ct);
            scored.Add(Score(timestamps[index], gray, SampleWidth, SampleHeight));
            progress?.Report(new MediaProgress("analyzing candidate frames",
                5 + 55d * (index + 1) / timestamps.Count,
                $"{index + 1} of {timestamps.Count}"));
        }

        var selected = Select(scored, quantity, diversity, metadata.DurationSeconds);
        var frames = new List<ExtractedFrame>(selected.Count);
        for (var index = 0; index < selected.Count; index++)
        {
            var item = selected[index];
            var jpeg = await ExtractPreviewAsync(path, item.Timestamp, ct);
            frames.Add(new ExtractedFrame(
                $"f-{Math.Round(item.Timestamp * 1000):0}", item.Timestamp,
                (long)Math.Round(item.Timestamp * metadata.FrameRate), jpeg,
                item.Hash, item.Quality, item.Warning));
            progress?.Report(new MediaProgress("building filmstrip",
                60 + 30d * (index + 1) / selected.Count,
                $"{index + 1} of {selected.Count}"));
        }

        var cropFrame = selected.Count > 0 ? selected[0] : scored[0];
        var cropGray = await ExtractGrayAsync(path, cropFrame.Timestamp, 160, 90, ct);
        var crop = DetectCrop(cropGray, 160, 90, metadata);
        var confidence = crop.Confidence >= .78 ? "high" : crop.Confidence >= .48 ? "medium" : "low";
        if (confidence == "low")
        {
            crop = PixelCrop.Full(metadata) with { Confidence = crop.Confidence };
        }
        progress?.Report(new MediaProgress("ready", 100, $"{frames.Count} reference frames"));
        return new VideoReferenceAnalysis(metadata, frames, crop, confidence, useScenes);
    }

    public static async Task<ExtractedFrame> CaptureAsync(
        string path, double timestampSeconds, VideoMetadata metadata, CancellationToken ct = default)
    {
        var timestamp = Math.Clamp(timestampSeconds, 0, Math.Max(0, metadata.DurationSeconds - .001));
        var gray = await ExtractGrayAsync(path, timestamp, SampleWidth, SampleHeight, ct);
        var score = Score(timestamp, gray, SampleWidth, SampleHeight);
        return new ExtractedFrame(
            $"f-{Math.Round(timestamp * 1000):0}", timestamp,
            (long)Math.Round(timestamp * metadata.FrameRate),
            await ExtractPreviewAsync(path, timestamp, ct), score.Hash, score.Quality, score.Warning);
    }

    public static Task<byte[]> RenderPngAsync(
        string path, double timestampSeconds, PixelCrop crop, CancellationToken ct = default)
    {
        EnsureVideo(path);
        var filter = crop.Method == "none"
            ? "format=rgb24"
            : FormattableString.Invariant($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y},format=rgb24");
        return CaptureBytesAsync(
            ["-v", "error", "-ss", timestampSeconds.ToString("0.######", CultureInfo.InvariantCulture),
             "-i", path, "-frames:v", "1", "-vf", filter, "-f", "image2pipe", "-vcodec", "png", "pipe:1"], ct);
    }

    internal static IReadOnlyList<ProxyScore> Select(
        IReadOnlyList<ProxyScore> candidates, int quantity, string diversity, double duration)
    {
        var acceptable = candidates.Where(candidate => candidate.Warning is null).ToList();
        if (acceptable.Count < quantity)
        {
            acceptable = candidates.OrderByDescending(candidate => candidate.Quality).ToList();
        }

        var selected = new List<ProxyScore>();
        var pool = acceptable.OrderByDescending(candidate => candidate.Quality).ToList();
        while (selected.Count < quantity && pool.Count > 0)
        {
            ProxyScore next;
            if (diversity.Equals("even", StringComparison.OrdinalIgnoreCase))
            {
                var target = duration * (selected.Count + 1) / (quantity + 1);
                next = pool.MinBy(candidate => Math.Abs(candidate.Timestamp - target))!;
            }
            else
            {
                next = pool.MaxBy(candidate =>
                {
                    var temporal = selected.Count == 0 ? 1
                        : selected.Min(existing => Math.Abs(existing.Timestamp - candidate.Timestamp))
                            / Math.Max(1, duration / quantity);
                    var visual = selected.Count == 0 ? 1
                        : selected.Min(existing => Hamming(existing.Hash, candidate.Hash)) / 64d;
                    var keyWeight = diversity.Equals("key", StringComparison.OrdinalIgnoreCase) ? 1.25 : 1;
                    return candidate.Quality / 100d + Math.Min(1, temporal) + keyWeight * visual;
                })!;
            }
            selected.Add(next);
            pool.RemoveAll(candidate =>
                Math.Abs(candidate.Timestamp - next.Timestamp) < .4
                || Hamming(candidate.Hash, next.Hash) < 7);
        }
        return selected.OrderBy(candidate => candidate.Timestamp).ToList();
    }

    internal sealed record ProxyScore(
        double Timestamp, ulong Hash, double Quality, double Brightness, double Sharpness, string? Warning);

    internal static ProxyScore Score(double timestamp, byte[] pixels, int width, int height)
    {
        if (pixels.Length < width * height)
        {
            return new ProxyScore(timestamp, 0, 0, 0, 0, "Frame could not be decoded");
        }
        var brightness = pixels.Average(value => (double)value);
        double laplacian = 0;
        var count = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var index = y * width + x;
                var value = 4 * pixels[index] - pixels[index - 1] - pixels[index + 1]
                    - pixels[index - width] - pixels[index + width];
                laplacian += value * value;
                count++;
            }
        }
        var sharpness = count == 0 ? 0 : Math.Sqrt(laplacian / count);
        var hash = DifferenceHash(pixels, width, height);
        var warning = brightness < 12 ? "Mostly black"
            : brightness > 246 ? "Mostly blank"
            : sharpness < 9 ? "Possibly blurry"
            : null;
        var exposure = 1 - Math.Min(1, Math.Abs(brightness - 128) / 128);
        var quality = Math.Clamp(55 * exposure + 45 * Math.Min(1, sharpness / 55), 0, 100);
        return new ProxyScore(timestamp, hash, quality, brightness, sharpness, warning);
    }

    internal static PixelCrop DetectCrop(byte[] gray, int width, int height, VideoMetadata metadata)
    {
        if (gray.Length < width * height)
        {
            return PixelCrop.Full(metadata) with { Confidence = 0 };
        }
        var maxX = Math.Max(1, width / 7);
        var maxY = Math.Max(1, height / 6);
        var left = UniformMargin(gray, width, height, true, false, maxX);
        var right = UniformMargin(gray, width, height, true, true, maxX);
        var top = UniformMargin(gray, width, height, false, false, maxY);
        var bottom = UniformMargin(gray, width, height, false, true, maxY);

        // A browser's content boundary is commonly the strongest stable horizontal edge in
        // the top 4–16%. Apply only when it is decisively stronger than the surrounding rows.
        var energies = Enumerable.Range(Math.Max(top + 1, height / 25), Math.Max(1, height / 6 - height / 25))
            .Where(y => y > 0 && y < height)
            .Select(y => (Y: y, Energy: RowEdge(gray, width, y)))
            .ToList();
        var median = energies.OrderBy(item => item.Energy).ElementAt(energies.Count / 2).Energy;
        var boundary = energies.MaxBy(item => item.Energy);
        var browserDetected = boundary.Energy > Math.Max(18, median * 2.4);
        if (browserDetected)
        {
            top = Math.Max(top, boundary.Y);
        }

        var scaleX = metadata.Width / (double)width;
        var scaleY = metadata.Height / (double)height;
        var x = (int)Math.Round(left * scaleX);
        var y = (int)Math.Round(top * scaleY);
        var cropWidth = metadata.Width - x - (int)Math.Round(right * scaleX);
        var cropHeight = metadata.Height - y - (int)Math.Round(bottom * scaleY);
        var removed = 1 - cropWidth * cropHeight / (double)(metadata.Width * metadata.Height);
        var confidence = removed <= .002 ? .25
            : browserDetected ? .82
            : (left + right + top + bottom) > 2 ? .7 : .35;
        return cropWidth > 0 && cropHeight > 0
            ? new PixelCrop(x, y, cropWidth, cropHeight, "automatic", confidence)
            : PixelCrop.Full(metadata) with { Confidence = 0 };
    }

    private static int UniformMargin(byte[] gray, int width, int height, bool vertical, bool reverse, int max)
    {
        var found = 0;
        for (var offset = 0; offset < max; offset++)
        {
            var values = vertical
                ? Enumerable.Range(0, height).Select(y => gray[y * width + (reverse ? width - 1 - offset : offset)])
                : Enumerable.Range(0, width).Select(x => gray[(reverse ? height - 1 - offset : offset) * width + x]);
            var array = values.Select(value => (double)value).ToArray();
            var mean = array.Average();
            var deviation = Math.Sqrt(array.Average(value => (value - mean) * (value - mean)));
            if (deviation > 5.5)
            {
                break;
            }
            found++;
        }
        return found;
    }

    private static double RowEdge(byte[] gray, int width, int y)
    {
        double sum = 0;
        for (var x = 0; x < width; x++)
        {
            sum += Math.Abs(gray[y * width + x] - gray[(y - 1) * width + x]);
        }
        return sum / width;
    }

    private static ulong DifferenceHash(byte[] pixels, int width, int height)
    {
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
        {
            var sourceY = Math.Min(height - 1, (int)Math.Round(y * (height - 1) / 7d));
            for (var x = 0; x < 8; x++)
            {
                var leftX = Math.Min(width - 1, (int)Math.Round(x * (width - 1) / 8d));
                var rightX = Math.Min(width - 1, (int)Math.Round((x + 1) * (width - 1) / 8d));
                if (pixels[sourceY * width + leftX] > pixels[sourceY * width + rightX])
                {
                    hash |= 1UL << (y * 8 + x);
                }
            }
        }
        return hash;
    }

    private static int Hamming(ulong left, ulong right) => System.Numerics.BitOperations.PopCount(left ^ right);

    private static IEnumerable<double> RegularTimestamps(double duration, int count)
    {
        for (var index = 0; index < count; index++)
        {
            yield return Math.Max(.05, duration * (index + 1) / (count + 1));
        }
    }

    private static async Task<IReadOnlyList<double>> DetectSceneChangesAsync(
        string path, double duration, CancellationToken ct)
    {
        try
        {
            var (_, _, stderr) = await Ffmpeg.RunCaptureAsync(Ffmpeg.Require(),
                ["-hide_banner", "-i", path, "-vf", "scale=320:-2,select='gt(scene,0.22)',showinfo",
                 "-vsync", "vfr", "-f", "null", "-"], ct, allowNonZeroExit: true);
            return SceneTimeRegex().Matches(stderr)
                .Select(match => double.Parse(match.Groups["time"].Value, CultureInfo.InvariantCulture))
                .Where(time => time >= 0 && time <= duration)
                .DistinctBy(time => Math.Round(time, 2))
                .Take(MaxCandidates)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static Task<byte[]> ExtractGrayAsync(
        string path, double timestamp, int width, int height, CancellationToken ct) =>
        CaptureBytesAsync(
            ["-v", "error", "-ss", timestamp.ToString("0.######", CultureInfo.InvariantCulture),
             "-i", path, "-frames:v", "1", "-vf", $"scale={width}:{height},format=gray",
             "-f", "rawvideo", "pipe:1"], ct);

    private static Task<byte[]> ExtractPreviewAsync(string path, double timestamp, CancellationToken ct) =>
        CaptureBytesAsync(
            ["-v", "error", "-ss", timestamp.ToString("0.######", CultureInfo.InvariantCulture),
             "-i", path, "-frames:v", "1", "-vf", "scale=640:-2:force_original_aspect_ratio=decrease",
             "-q:v", "4", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"], ct);

    private static async Task<byte[]> CaptureBytesAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var (_, bytes, _) = await Ffmpeg.RunCaptureAsync(Ffmpeg.Require(), arguments, ct);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("FFmpeg did not return a video frame at that timestamp.");
        }
        return bytes;
    }

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var parts = rate.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return 0;
        if (parts.Length == 1) return numerator;
        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0 ? numerator / denominator : 0;
    }

    private static void EnsureVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("The source video could not be found.", path);
        }
    }
}
