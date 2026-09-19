using Castmill.Media;

namespace Castmill.Media.Tests;

public sealed class VideoReferenceExtractorTests
{
    private const string SuppliedVideo =
        "/Users/jasonberes/Camtasia/React-Data-Grid-Accessibility/React-Data-Grid-Accessibility.mp4";

    [Fact]
    public void Quality_scoring_flags_blank_frames_and_hashes_visual_content()
    {
        var blank = Enumerable.Repeat((byte)255, 64 * 36).ToArray();
        var content = new byte[64 * 36];
        for (var y = 0; y < 36; y++)
        for (var x = 0; x < 64; x++)
            content[y * 64 + x] = (byte)(255 - x * 4);

        var blankScore = VideoReferenceExtractor.Score(0, blank, 64, 36);
        var contentScore = VideoReferenceExtractor.Score(1, content, 64, 36);

        Assert.Equal("Mostly blank", blankScore.Warning);
        Assert.NotEqual(blankScore.Hash, contentScore.Hash);
        Assert.True(contentScore.Quality > blankScore.Quality);
    }

    [Fact]
    public void Automatic_crop_detects_static_letterbox_margins()
    {
        const int width = 160, height = 90;
        var pixels = new byte[width * height];
        for (var y = 8; y < height - 6; y++)
        for (var x = 10; x < width - 12; x++)
            pixels[y * width + x] = (byte)(40 + (x * 3 + y * 5) % 180);

        var crop = VideoReferenceExtractor.DetectCrop(
            pixels, width, height, new VideoMetadata(10, 3840, 2160, 30));

        Assert.Equal("automatic", crop.Method);
        Assert.True(crop.X > 0);
        Assert.True(crop.Y > 0);
        Assert.True(crop.Width < 3840);
        Assert.True(crop.Height < 2160);
    }

    [Fact]
    public async Task Supplied_accessibility_video_extracts_distinct_lossless_reference_frames()
    {
        if (!File.Exists(SuppliedVideo)) return;

        var metadata = await VideoReferenceExtractor.ProbeAsync(
            SuppliedVideo, TestContext.Current.CancellationToken);
        Assert.Equal(3840, metadata.Width);
        Assert.Equal(2160, metadata.Height);
        Assert.InRange(metadata.DurationSeconds, 293, 295);
        Assert.InRange(metadata.FrameRate, 29.9, 30.1);

        var analysis = await VideoReferenceExtractor.AnalyzeAsync(
            SuppliedVideo, 5, "balanced", ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, analysis.Frames.Count);
        Assert.Equal(5, analysis.Frames.Select(frame => frame.PerceptualHash).Distinct().Count());
        Assert.All(analysis.Frames, frame =>
        {
            Assert.NotEmpty(frame.PreviewJpeg);
            Assert.Equal((byte)0xff, frame.PreviewJpeg[0]);
            Assert.Equal((byte)0xd8, frame.PreviewJpeg[1]);
            Assert.InRange(frame.TimestampSeconds, 0, metadata.DurationSeconds);
        });

        var first = analysis.Frames[0];
        var full = await VideoReferenceExtractor.RenderPngAsync(
            SuppliedVideo, first.TimestampSeconds, PixelCrop.Full(metadata),
            TestContext.Current.CancellationToken);
        Assert.True(full.Length > 100_000);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, full[..4]);
    }
}
