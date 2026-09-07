using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Images;
using Castmill.Core;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// ADR-055: one coherent image prompt, provider-native frames, content-aware crop. No
/// database, no Docker — these are the regressions that made the studio's images wrong.
/// </summary>
public sealed class ImagePromptComposerTests
{
    private static readonly Campaign Campaign = new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), OwnerId = Guid.NewGuid(),
        Name = "Launch", Brief = "Why **structured** UI knowledge beats scraped docs.",
    };

    private static ImageSlot Slot(string mode = "Auto", string? prompt = "warm light, the host at a desk") => new()
    {
        Kind = "youtube-thumbnail", State = "Empty", TargetWidth = 1280, TargetHeight = 720,
        PromptMode = mode, Prompt = prompt,
    };

    private static readonly Artifact Owner = new()
    {
        Kind = "blog", Title = "AI coding agents need structured UI knowledge",
        ContentJson = """{"content":{"title":"AI coding agents need structured UI knowledge","summary":"Component metadata beats scraped docs.","markdown":"# Why agents guess\n\nWithout structured component knowledge an agent reproduces whatever it saw last. [[cite:seg-1]]\n\n## What it looks like\n\nBody.","citations":["seg-1"]},"validation":{"passed":true}}""",
    };

    private static readonly BrandContext Brand = BrandContext.Empty with
    {
        ImageStyleBlock = "Brand look: terracotta and ink, editorial photography.",
    };

    [Fact]
    public void Auto_mode_is_one_short_brief_with_no_contradictory_rule_blocks()
    {
        var prompt = ImagePromptComposer.Compose(Slot(), Campaign, Owner, Brand);

        Assert.StartsWith("Create a YouTube thumbnail", prompt, StringComparison.Ordinal);
        Assert.Contains("Subject: AI coding agents need structured UI knowledge", prompt, StringComparison.Ordinal);
        Assert.Contains("What the piece says:", prompt, StringComparison.Ordinal);
        Assert.Contains("Sections: Why agents guess · What it looks like", prompt, StringComparison.Ordinal);
        Assert.Contains("Campaign brief: Why structured UI knowledge beats scraped docs.", prompt, StringComparison.Ordinal);
        Assert.Contains("Creative direction: warm light, the host at a desk", prompt, StringComparison.Ordinal);
        Assert.Contains("Brand look: terracotta and ink", prompt, StringComparison.Ordinal);

        // The old stacked blocks are gone: rules live in ImagePromptRules, appended by the renderer.
        Assert.DoesNotContain("middle 76%", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Text rendering rules", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("prefer wording", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("citations", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("seg-1", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < 1800, $"prompt too long: {prompt.Length}");
    }

    [Fact]
    public void Manual_mode_is_verbatim_and_the_adjustment_is_always_last()
    {
        var prompt = ImagePromptComposer.Compose(
            Slot("Manual", "a blue CRM with a presenter pointing"), Campaign, Owner, Brand, "warmer background");

        Assert.StartsWith("a blue CRM with a presenter pointing", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Brand look", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject:", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Adjustment: warmer background", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void References_are_described_only_when_attached()
    {
        var none = ImagePromptComposer.Compose(Slot(), Campaign, Owner, Brand);
        Assert.DoesNotContain("reference image", none, StringComparison.OrdinalIgnoreCase);

        var withProduct = ImagePromptComposer.Compose(Slot(), Campaign, Owner, Brand, null,
            [new ImageReference(Guid.NewGuid(), "shot.png", "image/png", [1], "product")]);
        Assert.Contains("1 reference image is attached, in this order", withProduct, StringComparison.Ordinal);
        // ADR-074: each image is numbered and given its job, not described as a group.
        Assert.Contains("- Image 1: the PRODUCT INTERFACE", withProduct, StringComparison.Ordinal);
        Assert.Contains("Never invent replacement UI", withProduct, StringComparison.Ordinal);
    }

    [Fact]
    public void Steering_from_a_legacy_take_strips_the_old_guardrails_and_appends_the_note()
    {
        const string legacy = "a hero image\nIf the image contains any text, prefer wording that uses \"react grid\" naturally. Never render a list of keywords.\nFinal composition target (follow exactly):\n- The published image is 1280×720\nText rendering rules (follow exactly):\n- Keep every word";
        var prompt = ImagePromptComposer.Steer(legacy, "more negative space top-left");

        Assert.Equal("a hero image\nAdjustment: more negative space top-left", prompt.Replace("\r", string.Empty));
    }

    [Fact]
    public void Providers_report_the_frame_they_really_paint()
    {
        // MAI paints exact sizes when both edges clear its 768 px floor. 1280×720 does not
        // (720 < 768), so it paints the largest legal 16:9 frame instead — same aspect to
        // within rounding, so the crop pass removes nothing but a pixel.
        var thumb = MaiImages.FrameFor("1280x720");
        Assert.Equal((1365, 768), thumb);
        Assert.InRange((double)thumb.Width / thumb.Height, 1.775, 1.780);
        Assert.Equal((1024, 768), MaiImages.FrameFor("1024x768"));
        // 1600×840 exceeds the 1 MP budget; the frame shrinks at the widest legal aspect (the
        // 768 px floor on the short edge caps it near 16:9), and the crop pass trims the rest.
        var header = MaiImages.FrameFor("1600x840");
        Assert.True(header.Width < 1600 && (long)header.Width * header.Height <= MaiImages.MaxPixels);
        Assert.True(header.Height >= MaiImages.MinEdge && header.Width >= MaiImages.MinEdge);
        Assert.InRange((double)header.Width / header.Height, 1.7, 1.92);

        // Gemini picks its nearest native ratio.
        Assert.Equal("16:9", GeminiImageProvider.AspectFor("1280x720"));
        Assert.Equal("16:9", GeminiImageProvider.AspectFor("1600x840"));
        Assert.Equal("1:1", GeminiImageProvider.AspectFor("1080x1080"));
        Assert.Equal((1344, 768), GeminiImageProvider.NativeFrames["16:9"]);

        // gpt-image keeps its three fixed frames whichever way the slot is described.
        Assert.Equal("1536x1024", OpenAiShapedImages.SizeFor("1280x720"));
        Assert.Equal("1536x1024", OpenAiShapedImages.SizeFor("16:9"));
        Assert.Equal("1024x1536", OpenAiShapedImages.SizeFor("1080x1920"));
        Assert.Equal((1024, 1024), ImageAspect.FixedFrame("1080x1080"));
    }

    [Fact]
    public void The_crop_follows_the_detail_instead_of_the_centre()
    {
        // 1536×1024 frame, 1280×720 slot: 171 px of overflow top+bottom. Paint the detail
        // (a dense checker) in the TOP band; a blind centre crop would slice it.
        using var bitmap = new SKBitmap(1536, 1024);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.DimGray);
            using var a = new SKPaint { Color = SKColors.White };
            using var b = new SKPaint { Color = SKColors.Black };
            for (var y = 0; y < 160; y += 8)
            {
                for (var x = 0; x < 1536; x += 8)
                {
                    canvas.DrawRect(x, y, 8, 8, ((x / 8) + (y / 8)) % 2 == 0 ? a : b);
                }
            }
        }
        using var scaled = bitmap.Copy();
        var (cropX, cropY) = ImageComposer.CropOffset(scaled, 1536, 864);
        Assert.Equal(0, cropX);
        Assert.True(cropY < 40, $"expected the window to hug the detailed top band, got y={cropY}");

        // A featureless frame keeps the centre.
        using var flat = new SKBitmap(1536, 1024);
        using (var canvas = new SKCanvas(flat)) { canvas.Clear(SKColors.DimGray); }
        Assert.Equal((0, 80), ImageComposer.CropOffset(flat, 1536, 864));
    }
}
