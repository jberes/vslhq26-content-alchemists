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
    /// <summary>Centre-crop to 9:16 for vertical platforms. Forces a re-encode.</summary>
    bool CropVertical,
    /// <summary>Transcript segments to burn as captions. Forces a re-encode.</summary>
    IReadOnlyList<TranscriptSegment>? Captions,
    string OutputDirectory);

/// <summary>
/// Desktop clip export (roadmap E7.5): stream-copy and re-encode modes, optional 9:16
/// centre crop, optional burned ASS captions, always <c>+faststart</c> so the file streams
/// before it has fully downloaded wherever it is posted.
/// </summary>
public static class ClipExporter
{
    public static async Task<string> ExportAsync(
        ClipExportRequest request, IProgress<MediaProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EndSeconds <= request.StartSeconds)
        {
            throw new ArgumentException("A clip must end after it starts.", nameof(request));
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var stem = Path.GetFileNameWithoutExtension(request.SourcePath);
        var output = Path.Combine(
            request.OutputDirectory,
            $"{stem}-clip-{(int)request.StartSeconds}s-{(int)request.EndSeconds}s{(request.CropVertical ? "-916" : "")}.mp4");

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
                    filters.Add("crop=ih*9/16:ih"); // centre crop; platform UI margins are the caption style's job
                }

                if (request.Captions is { Count: > 0 } captions)
                {
                    assPath = WriteAss(captions, request.StartSeconds, request.EndSeconds);
                    filters.Add($"ass={assPath}");
                }

                if (filters.Count > 0)
                {
                    arguments.AddRange(["-vf", string.Join(',', filters)]);
                }

                arguments.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac"]);
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
