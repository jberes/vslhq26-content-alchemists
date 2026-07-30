using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Castmill.Media;

/// <summary>A stage report from the engine; percent is null only when genuinely unknowable.</summary>
public sealed record MediaProgress(string Stage, double? Percent, string? Detail = null);

/// <summary>
/// Locates and runs ffmpeg. Resolution order: the app-managed sidecar directory (the
/// pinned-hash fetch script installs there — roadmap E7.2), then well-known system
/// locations. The engine never silently continues without ffmpeg: callers get a clear
/// exception naming what to install.
/// </summary>
public static class Ffmpeg
{
    private static readonly Regex TimeLine = new(
        @"time=(\d+):(\d{2}):(\d{2})\.(\d+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex DurationLine = new(
        @"Duration:\s*(\d+):(\d{2}):(\d{2})\.(\d+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Set by the host (shell) to the app-managed sidecar directory, if any.</summary>
    public static string? SidecarDirectory { get; set; }

    public static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        if (SidecarDirectory is { } sidecar)
        {
            var bundled = Path.Combine(sidecar, name);
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        foreach (var candidate in new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // PATH as last resort.
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return paths.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
    }

    public static string Require() =>
        Find() ?? throw new InvalidOperationException(
            "ffmpeg was not found. Install it (macOS: `brew install ffmpeg`) or run the "
            + "sidecar fetch script in tools/fetch-ffmpeg.sh, then try again.");

    /// <summary>Media duration, parsed from ffmpeg's own probe output — no ffprobe dependency.</summary>
    public static async Task<TimeSpan> ProbeDurationAsync(string inputPath, CancellationToken ct = default)
    {
        // `ffmpeg -i` with no output exits non-zero by design; the stderr still carries
        // the Duration line, which is all this needs.
        var (_, stderr) = await RunAsync(["-hide_banner", "-i", inputPath], null, null, ct, allowNonZeroExit: true);

        var match = DurationLine.Match(stderr);
        if (!match.Success)
        {
            throw new InvalidOperationException($"ffmpeg could not read a duration from {Path.GetFileName(inputPath)}.");
        }

        return ParseClock(match);
    }

    /// <summary>
    /// Runs ffmpeg, reporting progress by parsing the <c>time=</c> lines on stderr against
    /// a known total duration.
    /// </summary>
    public static Task<(int ExitCode, string StdErr)> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? totalDuration,
        IProgress<double>? progress,
        CancellationToken ct,
        bool allowNonZeroExit = false)
    {
        return Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = Require(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("ffmpeg failed to start.");

            var stderr = new StringBuilder();

            var pump = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
                {
                    stderr.AppendLine(line);

                    if (progress is not null && totalDuration is { TotalSeconds: > 0 } total)
                    {
                        var match = TimeLine.Match(line);
                        if (match.Success)
                        {
                            progress.Report(Math.Clamp(
                                ParseClock(match).TotalSeconds / total.TotalSeconds, 0, 1));
                        }
                    }
                }
            }, ct);

            await process.WaitForExitAsync(ct);
            await pump;

            if (process.ExitCode != 0 && !allowNonZeroExit)
            {
                var tail = stderr.ToString();
                throw new InvalidOperationException(
                    $"ffmpeg exited with {process.ExitCode}: {tail[^Math.Min(500, tail.Length)..]}");
            }

            return (process.ExitCode, stderr.ToString());
        }, ct);
    }

    private static TimeSpan ParseClock(Match match)
    {
        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var fraction = double.Parse("0." + match.Groups[4].Value, CultureInfo.InvariantCulture);
        return new TimeSpan(hours, minutes, seconds) + TimeSpan.FromSeconds(fraction);
    }
}

/// <summary>Extracts mono 16 kHz WAV — Whisper's required input — from any audio/video file.</summary>
public static class AudioExtractor
{
    public static async Task<string> ExtractAsync(
        string inputPath, IProgress<MediaProgress>? progress, CancellationToken ct = default)
    {
        var duration = await Ffmpeg.ProbeDurationAsync(inputPath, ct);
        var wavPath = Path.Combine(Path.GetTempPath(), $"castmill-{Guid.NewGuid():N}.wav");

        var percent = new Progress<double>(p =>
            progress?.Report(new MediaProgress("extracting audio", p * 100)));

        await Ffmpeg.RunAsync(
            ["-y", "-hide_banner", "-i", inputPath, "-vn", "-ac", "1", "-ar", "16000", "-f", "wav", wavPath],
            duration, percent, ct);

        progress?.Report(new MediaProgress("extracting audio", 100));
        return wavPath;
    }
}
