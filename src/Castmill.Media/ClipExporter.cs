using System.Globalization;
using System.Text;
using Castmill.Core.Ai;

namespace Castmill.Media;

public sealed record ClipExportRequest(
    string SourcePath,
    double StartSeconds,
    double EndSeconds,
    /// <summary>Frame-accurate re-encode; false = fast stream copy (keyframe-aligned cuts).</summary>
    bool ReEncode,
    /// <summary>Reframe to vertical 1080×1920 for Shorts/Reels/TikTok. Forces a re-encode.</summary>
    bool CropVertical,
    /// <summary>Transcript segments to burn as captions. Forces a re-encode.</summary>
    IReadOnlyList<TranscriptSegment>? Captions,
    string OutputDirectory,
    /// <summary>Written beside the clip as a .txt so the upload form can be filled from it.</summary>
    ClipMetadata? Metadata = null,
    /// <summary>Overrides the derived file stem, so a batch reads as clip-01, clip-02…</summary>
    string? OutputName = null);

/// <summary>Per-clip publishing copy — the title/description/tags an upload form wants.</summary>
public sealed record ClipMetadata(
    string? Title, string? Description, IReadOnlyList<string>? Hashtags, string? Hook);

/// <summary>
/// Desktop clip export (roadmap E7.5): stream-copy and re-encode modes, optional vertical
/// reframe to a platform-exact 1080×1920, optional burned ASS captions, always
/// <c>+faststart</c> so the file streams before it has fully downloaded wherever it is posted.
/// </summary>
public static class ClipExporter
{
    /// <summary>Short-form canvas — YouTube Shorts, Reels and TikTok all take 1080×1920.</summary>
    public const int VerticalWidth = 1080;
    public const int VerticalHeight = 1920;

    /// <summary>
    /// Scale-to-cover, then crop to the exact canvas. The earlier <c>crop=ih*9/16:ih</c>
    /// cropped in SOURCE pixels, so 1920×1080 came out 608×1080 — the right shape at the
    /// wrong resolution, which every platform then re-encodes. It also hard-failed on any
    /// source TALLER than 9:16 (720×1600 asks for a 900 px crop from a 720 px frame:
    /// "Invalid too big or non positive size for width"). Cover-then-crop yields exactly
    /// 1080×1920 from landscape, square and tall sources alike — all three measured.
    /// </summary>
    internal static string VerticalFilter =>
        $"scale={VerticalWidth}:{VerticalHeight}:force_original_aspect_ratio=increase," +
        $"crop={VerticalWidth}:{VerticalHeight},setsar=1";

    public static async Task<string> ExportAsync(
        ClipExportRequest request, IProgress<MediaProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EndSeconds <= request.StartSeconds)
        {
            throw new ArgumentException("A clip must end after it starts.", nameof(request));
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var stem = request.OutputName is { Length: > 0 } named
            ? Sanitize(named)
            : $"{Path.GetFileNameWithoutExtension(request.SourcePath)}-clip-" +
              $"{(int)request.StartSeconds}s-{(int)request.EndSeconds}s{(request.CropVertical ? "-916" : "")}";
        var output = Path.Combine(request.OutputDirectory, $"{stem}.mp4");

        var clipLength = TimeSpan.FromSeconds(request.EndSeconds - request.StartSeconds);
        var mustEncode = request.ReEncode || request.CropVertical || request.Captions is { Count: > 0 };

        string? assPath = null;
        var arguments = new List<string> { "-y", "-hide_banner" };

        try
        {
            if (mustEncode)
            {
                // -ss BEFORE -i seeks fast on the demuxer; the re-encode then makes the cut
                // frame-accurate. Captions are authored clip-relative, so they line up.
                arguments.AddRange(["-ss", Seconds(request.StartSeconds), "-i", request.SourcePath]);
                arguments.AddRange(["-t", Seconds(clipLength.TotalSeconds)]);

                var filters = new List<string>();
                if (request.CropVertical)
                {
                    filters.Add(VerticalFilter);
                }

                // Captions LAST: the ASS canvas is authored at 1080×1920, so it must be
                // burned after the reframe or the text scales with the crop.
                if (request.Captions is { Count: > 0 } captions)
                {
                    assPath = WriteAss(captions, request.StartSeconds, request.EndSeconds);
                    filters.Add($"ass={assPath}");
                }

                if (filters.Count > 0)
                {
                    arguments.AddRange(["-vf", string.Join(',', filters)]);
                }

                // yuv420p is the compatibility floor for every social player; without it a
                // 4:4:4 source encodes to a file some platforms refuse.
                arguments.AddRange(
                    ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                     "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k"]);
            }
            else
            {
                // Stream copy: instant, but cuts land on keyframes — the honest trade
                // stated in the UI as "fast" vs "frame-accurate".
                arguments.AddRange(["-ss", Seconds(request.StartSeconds), "-to", Seconds(request.EndSeconds)]);
                arguments.AddRange(["-i", request.SourcePath, "-c", "copy"]);
            }

            arguments.AddRange(["-movflags", "+faststart", output]);

            var percent = new Progress<double>(p =>
                progress?.Report(new MediaProgress(mustEncode ? "re-encoding clip" : "copying clip", p * 100)));

            await Ffmpeg.RunAsync(arguments, clipLength, percent, ct);

            if (request.Metadata is { } metadata)
            {
                await WriteMetadataAsync(output, metadata, request, ct);
            }

            progress?.Report(new MediaProgress("done", 100, output));
            return output;
        }
        finally
        {
            if (assPath is not null && File.Exists(assPath))
            {
                File.Delete(assPath);
            }
        }
    }

    /// <summary>
    /// Drops the clip's publishing copy beside the file. Uploading is manual until a
    /// channel integration exists, and retyping a title from another window is exactly the
    /// kind of friction that makes a generated clip go unused.
    /// </summary>
    private static async Task WriteMetadataAsync(
        string clipPath, ClipMetadata metadata, ClipExportRequest request, CancellationToken ct)
    {
        var sidecar = new StringBuilder();
        sidecar.AppendLine(metadata.Title ?? Path.GetFileNameWithoutExtension(clipPath));
        sidecar.AppendLine();

        if (!string.IsNullOrWhiteSpace(metadata.Description))
        {
            sidecar.AppendLine(metadata.Description);
            sidecar.AppendLine();
        }

        if (metadata.Hashtags is { Count: > 0 } tags)
        {
            sidecar.AppendLine(string.Join(' ', tags.Select(t => t.StartsWith('#') ? t : $"#{t}")));
            sidecar.AppendLine();
        }

        sidecar.AppendLine(CultureInfo.InvariantCulture,
            $"— cut from {Path.GetFileName(request.SourcePath)} at " +
            $"{TimeSpan.FromSeconds(request.StartSeconds):h\\:mm\\:ss}–{TimeSpan.FromSeconds(request.EndSeconds):h\\:mm\\:ss}");
        if (!string.IsNullOrWhiteSpace(metadata.Hook))
        {
            sidecar.AppendLine(CultureInfo.InvariantCulture, $"— hook: {metadata.Hook}");
        }

        await File.WriteAllTextAsync(
            Path.ChangeExtension(clipPath, ".txt"), sidecar.ToString(), ct);
    }

    /// <summary>Keeps a model-written title from becoming an illegal file name.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string(name
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '-' : c)
            .ToArray())
            .Trim();
        return cleaned.Length <= 80 ? cleaned : cleaned[..80].TrimEnd();
    }

    /// <summary>
    /// Writes an ASS subtitle file for the transcript segments overlapping the clip, times
    /// shifted to clip-relative. MarginV keeps text clear of platform UI chrome (E7.5's
    /// acceptance) and the style is deliberately plain: readable, outlined, bottom-centred.
    /// </summary>
    internal static string WriteAss(
        IReadOnlyList<TranscriptSegment> segments, double clipStart, double clipEnd)
    {
        var ass = new StringBuilder();
        ass.AppendLine("[Script Info]");
        ass.AppendLine("ScriptType: v4.00+");
        ass.AppendLine("PlayResX: 1080");
        ass.AppendLine("PlayResY: 1920");
        ass.AppendLine();
        ass.AppendLine("[V4+ Styles]");
        ass.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        // Alignment 2 = bottom-centre; MarginV 260 of 1920 clears TikTok/Reels chrome.
        ass.AppendLine("Style: Castmill,Arial,64,&H00F3F2F2,&H00202020,&H80000000,-1,3,1,2,60,60,260,1");
        ass.AppendLine();
        ass.AppendLine("[Events]");
        ass.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var segment in segments)
        {
            var start = Math.Max(segment.StartSeconds, clipStart) - clipStart;
            var end = Math.Min(segment.EndSeconds, clipEnd) - clipStart;
            if (end <= 0.05 || end <= start)
            {
                continue;
            }

            var text = segment.Text.Replace("\n", "\\N", StringComparison.Ordinal)
                                   .Replace("{", "(", StringComparison.Ordinal)
                                   .Replace("}", ")", StringComparison.Ordinal);
            ass.AppendLine(CultureInfo.InvariantCulture,
                $"Dialogue: 0,{AssClock(start)},{AssClock(end)},Castmill,,0,0,0,,{text}");
        }

        var path = Path.Combine(Path.GetTempPath(), $"castmill-{Guid.NewGuid():N}.ass");
        File.WriteAllText(path, ass.ToString());
        return path;
    }

    private static string Seconds(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string AssClock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }
}
