using System.ComponentModel.DataAnnotations;
using Castmill.Api.Services.Images;
using Castmill.Core.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// Docker-free units for the ADR-082 layer model: every assertion reads real pixels from the
/// composite (lossy WebP, hence the tolerances and the samples kept clear of hard edges).
/// </summary>
public sealed class OverlayLayerCompositionTests
{
    private static readonly SKColor Base = new(105, 105, 105);

    private static ImageComposer Composer() =>
        new(new ConfigurationBuilder().Build(), NullLogger<ImageComposer>.Instance);

    private static byte[] Png(int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static byte[] SplitPng(int w, int h, SKColor left, SKColor right)
    {
        using var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(left);
        using var paint = new SKPaint { Color = right };
        canvas.DrawRect(w / 2f, 0, w / 2f, h, paint);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static SKBitmap Compose(
        int size, SKColor background, IReadOnlyDictionary<Guid, byte[]>? layers, params OverlayBox[] boxes) =>
        ImageReferenceResolver.TryDecode(Composer().ComposeOverlay(Png(size, size, background), new OverlaySpec(boxes), layers).Image)!;

    private static OverlayBox Band(string id, double x, double y, double w, double h, string color) =>
        new(id, string.Empty, x, y, w, h, Kind: "text", Band: new OverlayBand(color, 1));

    private static OverlayBox Picture(string id, Guid assetId, double x, double y, double w, double h) =>
        new(id, string.Empty, x, y, w, h, LogoAssetId: assetId, Kind: "image");

    private static void Near(SKColor expected, SKColor actual, int tolerance = 4)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance
            && Math.Abs(expected.Green - actual.Green) <= tolerance
            && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected ~{expected}, got {actual}");
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
    private static bool IsBlue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;

    // ---- visibility, opacity, z-order ------------------------------------------------------

    [Fact]
    public void A_hidden_layer_is_not_drawn()
    {
        using var bitmap = Compose(400, Base, null, Band("a", 0.25, 0.25, 0.5, 0.5, "#FF0000") with { Visible = false });
        Near(Base, bitmap.GetPixel(200, 200), 1);
    }

    [Fact]
    public void Half_opacity_blends_the_layer_with_the_image_below()
    {
        using var bitmap = Compose(400, Base, null, Band("a", 0.25, 0.25, 0.5, 0.5, "#FF0000") with { Opacity = 0.5 });
        var pixel = bitmap.GetPixel(200, 200);
        Assert.InRange((int)pixel.Red, 165, 195);
        Assert.InRange((int)pixel.Green, 40, 65);
    }

    [Fact]
    public void A_later_layer_paints_over_an_earlier_one()
    {
        using var bitmap = Compose(400, Base, null,
            Band("back", 0.1, 0.1, 0.5, 0.5, "#FF0000"),
            Band("front", 0.3, 0.3, 0.5, 0.5, "#0000FF"));
        Assert.True(IsRed(bitmap.GetPixel(80, 80)), "only the back layer covers the top-left");
        Assert.True(IsBlue(bitmap.GetPixel(200, 200)), "the overlap shows the front layer");
    }

    // ---- image frames ----------------------------------------------------------------------

    [Fact]
    public void A_rounded_image_frame_clips_its_corners()
    {
        var id = Guid.NewGuid();
        using var bitmap = Compose(400, Base, new Dictionary<Guid, byte[]> { [id] = Png(300, 300, SKColors.Red) },
            Picture("p", id, 0.25, 0.25, 0.5, 0.5) with { Shape = "rounded", CornerRadius = 0.1 });
        Assert.True(IsRed(bitmap.GetPixel(200, 200)));
        Near(Base, bitmap.GetPixel(103, 103));
        Assert.True(IsRed(bitmap.GetPixel(200, 104)), "the straight edge is not clipped");
    }

    [Fact]
    public void A_circle_image_frame_clips_to_the_inscribed_oval()
    {
        var id = Guid.NewGuid();
        using var bitmap = Compose(400, Base, new Dictionary<Guid, byte[]> { [id] = Png(300, 300, SKColors.Red) },
            Picture("p", id, 0.25, 0.25, 0.5, 0.5) with { Shape = "circle" });
        Assert.True(IsRed(bitmap.GetPixel(200, 200)));
        Near(Base, bitmap.GetPixel(112, 112));
    }

    [Fact]
    public void A_rectangle_v2_image_frame_covers_rather_than_pillarboxing()
    {
        var id = Guid.NewGuid();
        // A SQUARE picture in a WIDE frame: the legacy box would fit it and leave bars.
        using var bitmap = Compose(1000, Base, new Dictionary<Guid, byte[]> { [id] = Png(200, 200, SKColors.Red) },
            Picture("p", id, 0.1, 0.4, 0.8, 0.2));
        Assert.True(IsRed(bitmap.GetPixel(115, 500)), "left end of the frame is covered");
        Assert.True(IsRed(bitmap.GetPixel(885, 500)), "right end of the frame is covered");
        Near(Base, bitmap.GetPixel(500, 380));
    }

    [Fact]
    public void Crop_focus_and_zoom_choose_which_part_of_the_picture_fills_the_frame()
    {
        var id = Guid.NewGuid();
        var layers = new Dictionary<Guid, byte[]> { [id] = SplitPng(200, 100, SKColors.Red, SKColors.Blue) };

        using var right = Compose(400, Base, layers,
            Picture("p", id, 0.25, 0.25, 0.5, 0.5) with { Crop = new OverlayImageCrop(FocusX: 1) });
        Assert.True(IsBlue(right.GetPixel(200, 200)));

        using var left = Compose(400, Base, layers,
            Picture("p", id, 0.25, 0.25, 0.5, 0.5) with { Crop = new OverlayImageCrop(FocusX: 0) });
        Assert.True(IsRed(left.GetPixel(200, 200)));

        // Same aspect as the picture: without zoom the centre is the seam; zoom 2 on the right half is all blue.
        using var zoomed = Compose(400, Base, layers,
            Picture("p", id, 0.1, 0.3, 0.8, 0.4) with { Crop = new OverlayImageCrop(FocusX: 1, Zoom: 2) });
        Assert.True(IsBlue(zoomed.GetPixel(60, 200)));
        Assert.True(IsBlue(zoomed.GetPixel(340, 200)));
    }

    [Fact]
    public void A_v2_image_layer_whose_bytes_are_missing_is_skipped()
    {
        using var bitmap = Compose(400, Base, null, Picture("p", Guid.NewGuid(), 0.1, 0.1, 0.5, 0.5));
        Near(Base, bitmap.GetPixel(200, 200), 1);
    }

    [Fact]
    public void Cover_source_is_centred_on_the_focus_and_clamped_inside_the_image()
    {
        var centred = ImageComposer.CoverSource(200, 100, 100, 100, new OverlayImageCrop());
        Assert.Equal(SKRect.Create(50, 0, 100, 100), centred);

        var clamped = ImageComposer.CoverSource(200, 100, 100, 100, new OverlayImageCrop(FocusX: 1));
        Assert.Equal(SKRect.Create(100, 0, 100, 100), clamped);

        var zoomed = ImageComposer.CoverSource(200, 100, 100, 100, new OverlayImageCrop(Zoom: 2));
        Assert.Equal(SKRect.Create(75, 25, 50, 50), zoomed);
    }

    // ---- border and effects ----------------------------------------------------------------

    [Fact]
    public void A_border_is_drawn_just_inside_the_frame_edge()
    {
        // 0.02 of 1000 px = a 20 px inset border.
        using var bitmap = Compose(1000, Base, null,
            Band("a", 0.2, 0.2, 0.6, 0.6, "#FF0000") with { Effect = new OverlayEffect("none", BorderWidth: 0.02, BorderColor: "#00FF00") });
        var border = bitmap.GetPixel(210, 500);
        Assert.True(border.Green > 200 && border.Red < 60, $"expected the border, got {border}");
        Assert.True(IsRed(bitmap.GetPixel(240, 500)), "inside the border is the band");
        Near(Base, bitmap.GetPixel(185, 500));
    }

    [Fact]
    public void A_sticker_gets_its_die_cut_border_even_when_border_width_is_zero()
    {
        // 0.0139 of 1000 px ≈ 14 px of white.
        using var bitmap = Compose(1000, Base, null,
            Band("a", 0.2, 0.2, 0.6, 0.6, "#FF0000") with { Effect = new OverlayEffect("sticker") });
        var edge = bitmap.GetPixel(206, 500);
        Assert.True(edge.Red > 230 && edge.Green > 230 && edge.Blue > 230, $"expected a white border, got {edge}");
        Assert.True(IsRed(bitmap.GetPixel(240, 500)));
    }

    [Theory]
    [InlineData("lift", 4, 20)]
    [InlineData("float", 25, 60)]
    public void A_drop_shadow_darkens_below_the_frame_but_not_above_it(string preset, int below, int above)
    {
        var light = new SKColor(220, 220, 220);
        using var bitmap = Compose(1000, light, null,
            Band("a", 0.3, 0.3, 0.4, 0.3, "#FFFFFF") with { Effect = new OverlayEffect(preset) });
        // Frame spans y 300..600.
        var under = bitmap.GetPixel(500, 600 + below);
        var over = bitmap.GetPixel(500, 300 - above);
        Assert.True(under.Red < light.Red - 15, $"expected shadow under the frame, got {under}");
        Near(light, over, 3);
    }

    [Fact]
    public void A_glow_brightens_every_side_of_the_frame()
    {
        var dark = new SKColor(30, 30, 30);
        using var bitmap = Compose(1000, dark, null,
            Band("a", 0.3, 0.3, 0.4, 0.4, "#202020") with { Effect = new OverlayEffect("glow") });
        foreach (var (x, y) in new[] { (500, 290), (500, 710), (290, 500), (710, 500) })
        {
            var pixel = bitmap.GetPixel(x, y);
            Assert.True(pixel.Red > dark.Red + 40 && pixel.Red > pixel.Blue, $"expected a warm glow at ({x},{y}), got {pixel}");
        }
    }

    [Fact]
    public void No_effect_leaves_pixels_outside_the_frame_untouched()
    {
        using var bitmap = Compose(1000, Base, null,
            Band("a", 0.3, 0.3, 0.4, 0.4, "#FFFFFF") with { Effect = new OverlayEffect("none") });
        foreach (var (x, y) in new[] { (500, 285), (500, 715), (285, 500), (715, 500) })
        {
            Near(Base, bitmap.GetPixel(x, y));
        }
    }

    [Fact]
    public void A_bevel_lightens_the_top_left_edge_and_darkens_the_bottom_right()
    {
        var mid = new SKColor(128, 128, 128);
        // Depth 1 doubles the default: an 11 px highlight and a 14 px shade on a 1000 px slot.
        using var bitmap = Compose(1000, new SKColor(40, 40, 40), null,
            Band("a", 0.2, 0.2, 0.6, 0.6, "#808080") with { Effect = new OverlayEffect("bevel", Depth: 1) });
        Assert.True(bitmap.GetPixel(205, 500).Red > mid.Red + 25, "left edge is lit");
        Assert.True(bitmap.GetPixel(500, 205).Red > mid.Red + 25, "top edge is lit");
        Assert.True(bitmap.GetPixel(794, 500).Red < mid.Red - 25, "right edge is shaded");
        Assert.True(bitmap.GetPixel(500, 794).Red < mid.Red - 25, "bottom edge is shaded");
        Near(mid, bitmap.GetPixel(500, 500));
    }

    [Fact]
    public void Preset_numbers_are_ratios_of_the_slot_height_scaled_by_depth()
    {
        var lift = ImageComposer.ShadowFor(new OverlayEffect("lift"), 1000)!.Value;
        Assert.Equal(5.6f, lift.Dy, 3);
        Assert.Equal(16.7f, lift.Blur, 3);
        Assert.Equal(97, lift.Color.Alpha);

        var deep = ImageComposer.ShadowFor(new OverlayEffect("float", Depth: 1), 1000)!.Value;
        Assert.Equal(66f, deep.Dy, 3);
        Assert.Equal(110f, deep.Blur, 3);

        var glow = ImageComposer.ShadowFor(new OverlayEffect("glow"), 1000)!.Value;
        Assert.Equal((0f, 0f), (glow.Dx, glow.Dy));
        Assert.Equal(new SKColor(0xFF, 0xD1, 0x66, 217), glow.Color);
        Assert.Equal(new SKColor(0x12, 0x34, 0x56, 217),
            ImageComposer.ShadowFor(new OverlayEffect("glow", BorderColor: "#123456"), 1000)!.Value.Color);

        Assert.Null(ImageComposer.ShadowFor(new OverlayEffect("none"), 1000));
        Assert.Null(ImageComposer.ShadowFor(null, 1000));
        Assert.Equal(13.9f, ImageComposer.BorderWidthFor(new OverlayEffect("sticker"), 1000), 3);
        Assert.Equal(0f, ImageComposer.BorderWidthFor(new OverlayEffect("lift"), 1000));
    }

    // ---- text layers -----------------------------------------------------------------------

    [Fact]
    public void A_text_layer_band_fills_the_whole_box_not_just_the_text()
    {
        using var bitmap = Compose(1000, Base, null,
            new OverlayBox("t", "Hi", 0.1, 0.1, 0.8, 0.5, FontSize: 0.05, Align: "center", Kind: "text", Band: new OverlayBand("#FF0000")));
        Assert.True(IsRed(bitmap.GetPixel(110, 110)), "top-left corner of the box");
        Assert.True(IsRed(bitmap.GetPixel(890, 590)), "bottom-right corner of the box");
        Near(Base, bitmap.GetPixel(500, 620));
    }

    [Fact]
    public void Text_opacity_zero_draws_the_band_and_no_glyphs()
    {
        OverlayBox Layer(double textOpacity) => new(
            "t", "HELLO WORLD", 0.1, 0.3, 0.8, 0.4, FontSize: 0.2, Weight: 800, Color: "#FFFFFF", Align: "center",
            Band: new OverlayBand("#0000FF"), Kind: "text", TextOpacity: textOpacity);

        static int Bright(SKBitmap bitmap)
        {
            var count = 0;
            for (var y = 310; y < 690; y += 3)
            {
                for (var x = 110; x < 890; x += 3)
                {
                    count += bitmap.GetPixel(x, y).Red > 200 ? 1 : 0;
                }
            }
            return count;
        }

        using var hidden = Compose(1000, Base, null, Layer(0));
        Assert.Equal(0, Bright(hidden));
        Assert.True(IsBlue(hidden.GetPixel(500, 500)));

        using var shown = Compose(1000, Base, null, Layer(1));
        Assert.True(Bright(shown) > 100, "control: the same layer at full text opacity draws glyphs");
    }

    [Theory]
    [InlineData("Barlow Condensed")]
    [InlineData("Barlow")]
    [InlineData("Anton")]
    [InlineData("DM Serif Display")]
    [InlineData("IBM Plex Mono")]
    public void Every_bundled_family_renders_without_falling_back(string family)
    {
        var spec = new OverlaySpec([
            new OverlayBox("t", "Deploy time, halved", 0.1, 0.3, 0.8, 0.4, FontSize: 0.12, Weight: 700,
                Color: "#FFFFFF", Kind: "text", FontFamily: family),
        ]);
        var result = Composer().ComposeOverlay(Png(800, 450, SKColors.Black), spec);

        Assert.False(result.FontFallback);
        using var bitmap = ImageReferenceResolver.TryDecode(result.Image)!;
        var bright = 0;
        for (var y = 135; y < 315; y += 2)
        {
            for (var x = 80; x < 720; x += 2)
            {
                bright += bitmap.GetPixel(x, y).Red > 200 ? 1 : 0;
            }
        }
        Assert.True(bright > 50, $"{family} drew glyphs");
    }

    [Fact]
    public void Every_bundled_face_file_loads_as_its_family()
    {
        var resolver = new OverlayFontResolver(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"));
        Assert.Equal(OverlayFonts.Families.Order(StringComparer.Ordinal), OverlayFontResolver.Faces.Keys.Order(StringComparer.Ordinal));
        foreach (var (family, faces) in OverlayFontResolver.Faces)
        {
            foreach (var (weight, _) in faces)
            {
                var resolved = resolver.Resolve(family, weight);
                Assert.NotNull(resolved);
                Assert.StartsWith(family, resolved.Value.Typeface.FamilyName, StringComparison.Ordinal);
                Assert.False(resolved.Value.Embolden);
            }
        }
    }

    [Theory]
    [InlineData("Barlow", 400, "Barlow-Regular.ttf", false)]
    [InlineData("Barlow", 500, "Barlow-Medium.ttf", false)]
    [InlineData("Barlow", 450, "Barlow-Medium.ttf", false)]
    [InlineData("Barlow", 550, "Barlow-SemiBold.ttf", false)]
    [InlineData("Barlow", 640, "Barlow-Bold.ttf", false)]
    [InlineData("Barlow", 350, "Barlow-Regular.ttf", false)]
    [InlineData("Barlow", 600, "Barlow-SemiBold.ttf", false)]
    [InlineData("Barlow", 650, "Barlow-Bold.ttf", false)]
    [InlineData("Barlow", 900, "Barlow-ExtraBold.ttf", true)]
    [InlineData("Barlow", 100, "Barlow-Regular.ttf", false)]
    [InlineData("Anton", 700, "Anton-Regular.ttf", true)]
    [InlineData("DM Serif Display", 450, "DMSerifDisplay-Regular.ttf", false)]
    [InlineData("DM Serif Display", 500, "DMSerifDisplay-Regular.ttf", true)]
    [InlineData("IBM Plex Mono", 800, "IBMPlexMono-Bold.ttf", true)]
    [InlineData(null, 600, "BarlowCondensed-SemiBold.ttf", false)]
    [InlineData(null, 750, "BarlowCondensed-ExtraBold.ttf", false)]
    public void The_face_is_matched_like_css_and_heavier_requests_are_emboldened(
        string? family, int weight, string file, bool embolden)
    {
        var selected = OverlayFontResolver.Select(family, weight);
        Assert.Equal((file, embolden), (selected.File, selected.Embolden));
    }

    [Fact]
    public void A_family_whose_files_are_missing_resolves_to_nothing()
    {
        var empty = Directory.CreateTempSubdirectory("castmill-fonts-");
        try
        {
            Assert.Null(new OverlayFontResolver(empty.FullName).Resolve("Anton", 400));
        }
        finally
        {
            empty.Delete();
        }
    }

    [Fact]
    public void Long_text_shrinks_until_it_fits_and_never_extends_below_the_box()
    {
        const string text = "Deploy time, halved for every enterprise React team on the planet, measured over six months";
        using var font = new SKFont(SKTypeface.FromFile(ImageComposer.DefaultFontPath), 300);
        var frame = SKRect.Create(100, 100, 800, 200);
        var layout = ImageComposer.LayoutText(font, text, frame, null);
        Assert.True(layout.Size < 300);
        Assert.True(layout.Lines.Count * layout.LineHeight <= frame.Height - (2 * layout.PadY));
        Assert.Equal(layout.Size * 0.35f, layout.PadX, 3);
        Assert.Equal(layout.Size * 1.1f, layout.LineHeight, 3);

        using var bitmap = Compose(1000, SKColors.Black, null,
            new OverlayBox("t", text, 0.1, 0.1, 0.8, 0.2, FontSize: 0.3, Color: "#FFFFFF", Kind: "text"));
        var inside = 0;
        for (var x = 100; x < 900; x++)
        {
            for (var y = 305; y < 330; y++)
            {
                Assert.True(bitmap.GetPixel(x, y).Red < 40, $"text below the box at ({x},{y})");
            }
            for (var y = 110; y < 290; y += 4)
            {
                inside += bitmap.GetPixel(x, y).Red > 200 ? 1 : 0;
            }
        }
        Assert.True(inside > 50, "the text itself was drawn inside the box");
    }

    [Fact]
    public void A_legacy_box_next_to_a_v2_layer_still_sizes_its_band_to_the_text()
    {
        using var bitmap = Compose(1000, Base, null,
            new OverlayBox("legacy", "Hi", 0.1, 0.1, 0.8, 0.5, FontSize: 0.05, Band: new OverlayBand("#FF0000", 1)),
            Band("v2", 0.1, 0.7, 0.2, 0.2, "#0000FF"));
        Near(Base, bitmap.GetPixel(880, 580));
        Assert.True(IsBlue(bitmap.GetPixel(200, 800)));
    }

    // ---- validation ------------------------------------------------------------------------

    private static List<ValidationResult> Validate(OverlaySpec spec)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(spec, new ValidationContext(spec), results, validateAllProperties: true);
        return results;
    }

    private static OverlayBox ValidText(string id = "t") => new(
        id, "Deploy time, halved", 0.1, 0.1, 0.8, 0.3, Kind: "text", Name: "Headline", Opacity: 0.9,
        FontFamily: "Anton", TextOpacity: 0.8, CornerRadius: 0.02,
        Band: new OverlayBand("#000000", 0.6), Effect: new OverlayEffect("lift", 0.5, 0.01, "#FFFFFF"));

    private static OverlayBox ValidImage(string id = "i") => new(
        id, string.Empty, -0.2, 0.5, 0.4, 0.4, LogoAssetId: Guid.NewGuid(), Kind: "image", Shape: "rounded",
        CornerRadius: 0.05, Crop: new OverlayImageCrop(0.3, 0.6, 1.5), Effect: new OverlayEffect("sticker"));

    [Fact]
    public void A_valid_v2_spec_passes_validation()
    {
        Assert.Empty(Validate(new OverlaySpec([ValidText(), ValidImage()])));
    }

    [Fact]
    public void Twenty_four_layers_pass_validation()
    {
        Assert.Empty(Validate(new OverlaySpec([.. Enumerable.Range(0, OverlaySpec.MaxBoxes).Select(i => ValidText($"t{i}"))])));
    }

    private static readonly Dictionary<string, Func<OverlaySpec>> InvalidSpecs = new(StringComparer.Ordinal)
    {
        ["Boxes[0].Effect.Preset"] = () => new OverlaySpec([ValidText() with { Effect = new OverlayEffect("explode") }]),
        ["Boxes[0].Kind"] = () => new OverlaySpec([ValidText() with { Kind = "video" }]),
        ["Boxes[0].Shape"] = () => new OverlaySpec([ValidImage() with { Shape = "hexagon" }]),
        ["Boxes[0].Align"] = () => new OverlaySpec([ValidText() with { Align = "justify" }]),
        ["Boxes[0].FontFamily"] = () => new OverlaySpec([ValidText() with { FontFamily = "Comic Sans" }]),
        ["Boxes[1].Id"] = () => new OverlaySpec([ValidText("same"), ValidImage("same")]),
        ["Boxes[0].Opacity"] = () => new OverlaySpec([ValidText() with { Opacity = 1.5 }]),
        ["Boxes[0].Effect.Depth"] = () => new OverlaySpec([ValidText() with { Effect = new OverlayEffect("lift", Depth: 2) }]),
        ["Boxes[0].Crop.Zoom"] = () => new OverlaySpec([ValidImage() with { Crop = new OverlayImageCrop(Zoom: 9) }]),
        ["Boxes[0].Band.Opacity"] = () => new OverlaySpec([ValidText() with { Band = new OverlayBand("#000000", -1) }]),
        ["Boxes[0].LogoAssetId"] = () => new OverlaySpec([ValidImage() with { LogoAssetId = null }]),
        ["Boxes"] = () => new OverlaySpec([.. Enumerable.Range(0, OverlaySpec.MaxBoxes + 1).Select(i => ValidText($"t{i}"))]),
    };

    [Theory]
    [InlineData("Boxes[0].Effect.Preset")]
    [InlineData("Boxes[0].Kind")]
    [InlineData("Boxes[0].Shape")]
    [InlineData("Boxes[0].Align")]
    [InlineData("Boxes[0].FontFamily")]
    [InlineData("Boxes[1].Id")]
    [InlineData("Boxes[0].Opacity")]
    [InlineData("Boxes[0].Effect.Depth")]
    [InlineData("Boxes[0].Crop.Zoom")]
    [InlineData("Boxes[0].Band.Opacity")]
    [InlineData("Boxes[0].LogoAssetId")]
    [InlineData("Boxes")]
    public void An_invalid_value_fails_validation_naming_its_path(string path)
    {
        var results = Validate(InvalidSpecs[path]());
        Assert.Contains(results, r => r.MemberNames.Contains(path));
    }
}
