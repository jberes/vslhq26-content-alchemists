using System.Globalization;
using Castmill.Core.Ai;
using Castmill.Media;

namespace Castmill.Media.Tests;

/// <summary>
/// Engine tests for the local media pipeline (roadmap E7.2/E7.3/E7.5), run against REAL
/// tools: ffmpeg from the machine, a real Whisper model, real synthesized speech. They
/// skip — loudly, not silently pass — when ffmpeg is absent, so CI without media tooling
/// stays green while a dev machine exercises the whole path.
///
/// The Whisper test uses the tiny model (~75 MB, cached across runs in the user cache
/// dir); first run downloads it.
/// </summary>
public sealed class MediaEngineTests
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "castmill-tests");

    [Fact]
    public async Task Ffmpeg_probes_duration_of_synthesized_audio()
    {
        SkipUnlessToolingPresent();
        var media = await SynthesizeSpeechAsync();

        var duration = await Ffmpeg.ProbeDurationAsync(media);

        Assert.InRange(duration.TotalSeconds, 3, 60);
    }

    [Fact]
    public async Task Extraction_produces_the_wav_whisper_requires()
    {
        SkipUnlessToolingPresent();
        var media = await SynthesizeSpeechAsync();
        var stages = new List<string>();

        var wav = await AudioExtractor.ExtractAsync(
            media, new Progress<MediaProgress>(p => stages.Add(p.Stage)));

        try
        {
            Assert.True(File.Exists(wav));
            Assert.True(new FileInfo(wav).Length > 1000);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Whisper_transcribes_synthesized_speech_with_real_timestamps()
    {
        SkipUnlessToolingPresent();
        var media = await SynthesizeSpeechAsync();

        var models = new WhisperModelManager(Path.Combine(CacheDir, "whisper"));
        var modelPath = await models.EnsureAsync("tiny", null, TestContext.Current.CancellationToken);

        var segments = await WhisperTranscriber.TranscribeAsync(
            media, modelPath, null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(segments);

        // Canonical ids and monotonic real timings — the provenance backbone.
        Assert.Equal("s01", segments[0].Id);
        Assert.True(segments[0].StartSeconds >= 0);
        Assert.True(segments[^1].EndSeconds > segments[0].StartSeconds);

        // tiny is a small model, so assert on unmistakable words, not exact prose.
        var text = string.Join(" ", segments.Select(s => s.Text)).ToLowerInvariant();
        Assert.Contains("pipeline", text);
        Assert.Contains("deploy", text);
    }

    [Fact]
    public async Task Clip_export_cuts_a_vertical_captioned_clip_that_ffmpeg_can_read_back()
    {
        SkipUnlessToolingPresent();
        var video = await SynthesizeVideoAsync();
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");

        var captions = new List<TranscriptSegment>
        {
            new("s01", 1.0, 3.5, "HOST", "We shipped the pipeline."),
            new("s02", 3.5, 6.0, null, "It cut deploy time in half."),
        };

        var output = await ClipExporter.ExportAsync(
            new ClipExportRequest(video, 1.0, 6.0, ReEncode: true, CropVertical: true,
                captions, outputDir),
            null,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(File.Exists(output));

            // Read the result back with ffmpeg: duration ≈ 5 s proves the cut; a decodable
            // file proves the crop+ass filter chain didn't corrupt the stream.
            var duration = await Ffmpeg.ProbeDurationAsync(output);
            Assert.InRange(duration.TotalSeconds, 4.0, 6.5);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stream_copy_export_is_supported_for_fast_cuts()
    {
        SkipUnlessToolingPresent();
        var video = await SynthesizeVideoAsync();
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");

        var output = await ClipExporter.ExportAsync(
            new ClipExportRequest(video, 0, 4.0, ReEncode: false, CropVertical: false,
                Captions: null, outputDir),
            null,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(File.Exists(output));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Vertical_export_is_exactly_1080x1920_from_a_landscape_source()
    {
        SkipUnlessToolingPresent();
        var video = await SynthesizeVideoAsync(); // 1280×720
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");

        var output = await ClipExporter.ExportAsync(
            new ClipExportRequest(video, 1.0, 4.0, ReEncode: true, CropVertical: true,
                Captions: null, outputDir),
            null,
            TestContext.Current.CancellationToken);

        try
        {
            // The old crop cut in SOURCE pixels, so 1280×720 came out 405×720 — the right
            // shape at the wrong resolution. Platforms re-encode anything off-spec.
            var (width, height) = await ProbeSizeAsync(output);
            Assert.Equal(ClipExporter.VerticalWidth, width);
            Assert.Equal(ClipExporter.VerticalHeight, height);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Vertical_export_survives_a_source_taller_than_9_by_16()
    {
        SkipUnlessToolingPresent();
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");
        var tall = Path.Combine(outputDir, "tall-source.mp4");
        Directory.CreateDirectory(outputDir);
        await Ffmpeg.RunAsync(
            ["-y", "-f", "lavfi", "-i", "testsrc2=size=720x1600:rate=30:duration=4",
             "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", tall],
            TimeSpan.FromSeconds(4), null, TestContext.Current.CancellationToken);

        try
        {
            // crop=ih*9/16:ih asked for a 900 px crop from a 720 px frame and ffmpeg
            // aborted: "Invalid too big or non positive size for width".
            var output = await ClipExporter.ExportAsync(
                new ClipExportRequest(tall, 0.5, 3.0, ReEncode: true, CropVertical: true,
                    Captions: null, outputDir),
                null,
                TestContext.Current.CancellationToken);

            var (width, height) = await ProbeSizeAsync(output);
            Assert.Equal(ClipExporter.VerticalWidth, width);
            Assert.Equal(ClipExporter.VerticalHeight, height);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Publishing_copy_is_written_beside_the_clip()
    {
        SkipUnlessToolingPresent();
        var video = await SynthesizeVideoAsync();
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");

        var output = await ClipExporter.ExportAsync(
            new ClipExportRequest(video, 1.0, 3.0, ReEncode: false, CropVertical: false,
                Captions: null, outputDir,
                new ClipMetadata(
                    "Deploy time, halved",
                    "How the team cut the pipeline down.",
                    ["devops", "shipping"],
                    "It cut deploy time in half."),
                OutputName: "clip-01-vertical"),
            null,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal("clip-01-vertical.mp4", Path.GetFileName(output));

            var sidecar = Path.ChangeExtension(output, ".txt");
            var copy = await File.ReadAllTextAsync(sidecar, TestContext.Current.CancellationToken);
            Assert.StartsWith("Deploy time, halved", copy, StringComparison.Ordinal);
            Assert.Contains("#devops #shipping", copy, StringComparison.Ordinal);
            Assert.Contains("hook:", copy, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void Ass_subtitles_are_clip_relative_and_escape_override_braces()
    {
        // Pure function — no tooling needed.
        var path = ClipExporter.WriteAss(
        [
            new TranscriptSegment("s01", 10.0, 12.0, null, "Costs {half} now"),
            new TranscriptSegment("s02", 90.0, 95.0, null, "Outside the clip"),
        ], clipStart: 9.0, clipEnd: 14.0);

        try
        {
            var ass = File.ReadAllText(path);

            Assert.Contains("Dialogue: 0,0:00:01.00,0:00:03.00", ass); // shifted by -9 s
            Assert.Contains("Costs (half) now", ass);                  // {} would be ASS overrides
            Assert.DoesNotContain("Outside the clip", ass);            // not in range
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Captions used to be burned 260px from the bottom of a 1920px canvas — inside TikTok's
    /// own caption/handle/button stack, which occupies roughly the lowest 320px. Text partly
    /// underneath the platform UI is unreadable, and 85% of short-form is watched muted, so
    /// this is the content rather than a garnish.
    /// </summary>
    [Fact]
    public void Captions_are_placed_clear_of_the_platform_chrome()
    {
        var path = ClipExporter.WriteAss(
            [new TranscriptSegment("s01", 0, 2, null, "Deploy time, halved")],
            clipStart: 0, clipEnd: 3);

        try
        {
            var ass = File.ReadAllText(path);

            // The canvas the margins are relative to must match the output, or every size
            // in the style sheet is scaled against libass's 384×288 default.
            Assert.Contains($"PlayResX: {ClipExporter.VerticalWidth}", ass, StringComparison.Ordinal);
            Assert.Contains($"PlayResY: {ClipExporter.VerticalHeight}", ass, StringComparison.Ordinal);
            Assert.Contains("ScaledBorderAndShadow: yes", ass, StringComparison.Ordinal);

            // Clear of the ~320px bottom stack, with room to spare.
            Assert.True(ClipExporter.CaptionMarginV >= 400,
                $"Captions sit {ClipExporter.CaptionMarginV}px from the bottom — inside platform chrome.");
            Assert.Contains($",{ClipExporter.CaptionMarginV},", ass, StringComparison.Ordinal);

            // Big enough to read on a phone at arm's length.
            Assert.True(ClipExporter.CaptionFontSize >= 70,
                $"Caption size {ClipExporter.CaptionFontSize} is subtitle-sized, not short-form-sized.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Loudness, measured on the real output rather than asserted from the argument list.
    /// Platforms normalize to about -14 LUFS; a clip left at whatever the source happened to
    /// be arrives noticeably louder or quieter than the feed around it.
    /// </summary>
    [Fact]
    public async Task An_exported_clip_is_normalised_to_the_social_loudness_target()
    {
        SkipUnlessToolingPresent();
        var video = await SynthesizeVideoAsync();
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");

        var output = await ClipExporter.ExportAsync(
            new ClipExportRequest(video, 1.0, 8.0, ReEncode: true, CropVertical: true,
                Captions: null, outputDir),
            null,
            TestContext.Current.CancellationToken);

        try
        {
            var loudness = await MeasureLoudnessAsync(output);
            // Single-pass loudnorm lands close but not exactly on target; the point is that
            // it is in the social band rather than at broadcast -24.
            Assert.InRange(loudness, -18.0, -10.0);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// The blurred-pillarbox fallback, measured. A centre crop of a screen share or a wide
    /// two-shot throws the content away; this keeps the whole frame and fills the canvas with
    /// a blurred copy of it. It is a split/overlay graph, so the thing that actually goes
    /// wrong is the filtergraph failing to parse — which only a real run catches.
    /// </summary>
    [Fact]
    public async Task Pillarbox_reframe_produces_a_full_canvas_with_nothing_cropped_away()
    {
        SkipUnlessToolingPresent();
        var video = await SynthesizeVideoAsync(); // 1280×720
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");

        var output = await ClipExporter.ExportAsync(
            new ClipExportRequest(video, 1.0, 5.0, ReEncode: true, CropVertical: true,
                Captions: null, outputDir, Reframe: ReframeMode.BlurredPillarbox),
            null,
            TestContext.Current.CancellationToken);

        try
        {
            var (width, height) = await ProbeSizeAsync(output);
            Assert.Equal(ClipExporter.VerticalWidth, width);
            Assert.Equal(ClipExporter.VerticalHeight, height);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// Hook overlay, end card and cover frame together. drawtext takes a filtergraph
    /// argument, so a colon in a model-written hook breaks the whole filter string unless it
    /// is escaped — exactly the kind of thing that only fails against real ffmpeg.
    /// </summary>
    [Fact]
    public async Task A_hook_with_punctuation_an_end_card_and_a_cover_frame_all_render()
    {
        SkipUnlessToolingPresent();
        var video = await SynthesizeVideoAsync();
        var outputDir = Path.Combine(Path.GetTempPath(), $"castmill-clips-{Guid.NewGuid():N}");

        var output = await ClipExporter.ExportAsync(
            new ClipExportRequest(video, 1.0, 5.0, ReEncode: true, CropVertical: true,
                Captions: null, outputDir,
                HookOverlay: "Deploy time: halved — here's how (100% real)",
                EndCard: true,
                CoverFrame: true),
            null,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(File.Exists(output));

            // The end card holds the last frame, so the clip is LONGER than the cut.
            var duration = await Ffmpeg.ProbeDurationAsync(output);
            Assert.InRange(duration.TotalSeconds, 4.5, 6.5);

            var cover = Path.ChangeExtension(output, ".cover.jpg");
            Assert.True(File.Exists(cover), "no cover frame was written beside the clip");
            Assert.True(new FileInfo(cover).Length > 1000, "the cover frame is suspiciously small");
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    // ---- fixtures ---------------------------------------------------------------

    /// <summary>Integrated loudness (LUFS) of a file, via ffmpeg's EBU R128 meter.</summary>
    private static async Task<double> MeasureLoudnessAsync(string path)
    {
        var (_, stderr) = await Ffmpeg.RunAsync(
            ["-hide_banner", "-i", path, "-af", "ebur128=framelog=verbose", "-f", "null", "-"],
            null, null, TestContext.Current.CancellationToken, allowNonZeroExit: true);

        var match = System.Text.RegularExpressions.Regex.Match(stderr, @"I:\s*(-?\d+(?:\.\d+)?)\s*LUFS");
        Assert.True(match.Success, $"ffmpeg reported no integrated loudness. Output:\n{stderr[^Math.Min(600, stderr.Length)..]}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the encoded frame size back with ffprobe — the only honest check
    /// that a filter chain produced the canvas the platform expects.</summary>
    private static async Task<(int Width, int Height)> ProbeSizeAsync(string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(Ffmpeg.Find()!)!, "ffprobe");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height", "-of", "csv=p=0", path,
            },
        })!;

        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var parts = output.Trim().Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static void SkipUnlessToolingPresent()
    {
        Assert.SkipWhen(Ffmpeg.Find() is null,
            "ffmpeg is not installed on this machine — engine tests need it (brew install ffmpeg).");
        Assert.SkipUnless(OperatingSystem.IsMacOS(),
            "Speech synthesis fixture uses macOS `say`; on other systems provide a fixture file.");
    }

    /// <summary>~9 s of synthesized narration, cached per test session.</summary>
    private static async Task<string> SynthesizeSpeechAsync()
    {
        Directory.CreateDirectory(CacheDir);
        var m4a = Path.Combine(CacheDir, "fixture-speech.m4a");
        if (File.Exists(m4a))
        {
            return m4a;
        }

        var aiff = Path.Combine(CacheDir, "fixture-speech.aiff");
        await RunAsync("/usr/bin/say",
            ["-o", aiff,
             "We shipped the new deployment pipeline this quarter. It cut deploy time in half. " +
             "Customers noticed the dashboard first. Rollbacks are now a single command."]);
        await Ffmpeg.RunAsync(["-y", "-i", aiff, "-c:a", "aac", m4a], null, null, CancellationToken.None);
        File.Delete(aiff);
        return m4a;
    }

    /// <summary>A 10 s test-pattern MP4 with the synthesized narration as its audio track.</summary>
    private static async Task<string> SynthesizeVideoAsync()
    {
        Directory.CreateDirectory(CacheDir);
        var mp4 = Path.Combine(CacheDir, "fixture-video.mp4");
        if (File.Exists(mp4))
        {
            return mp4;
        }

        var audio = await SynthesizeSpeechAsync();
        await Ffmpeg.RunAsync(
            ["-y",
             "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=10",
             "-i", audio,
             "-shortest", "-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac", mp4],
            null, null, CancellationToken.None);
        return mp4;
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = fileName };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
