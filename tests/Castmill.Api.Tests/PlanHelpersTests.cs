using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Images;
using Castmill.Core.Resources;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>Docker-free units for ADR-055/056: overlay composition, mask helpers, briefs, claims.</summary>
public sealed class PlanHelpersTests
{
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

    [Fact]
    public void The_overlay_composite_draws_boxes_where_the_spec_says_and_wraps_long_text()
    {
        var composer = new ImageComposer(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageComposer>.Instance);
        var spec = new OverlaySpec([
            new OverlayBox("a", "Deploy time, halved for every enterprise React team on the planet", 0.1, 0.1, 0.5, 0.3, 0.08, 700, "#FFFFFF", "left",
                new OverlayBand("#000000", 1)),
        ]);
        var result = composer.ComposeOverlay(Png(1280, 720, SKColors.DimGray), spec);

        using var bitmap = ImageReferenceResolver.TryDecode(result.Image)!;
        Assert.Equal((1280, 720), (bitmap.Width, bitmap.Height));
        // Inside the band: black. Far outside every box: untouched grey.
        var inside = bitmap.GetPixel(130, 80);
        var outside = bitmap.GetPixel(1200, 650);
        Assert.True(inside.Red < 40 && inside.Green < 40 && inside.Blue < 40, $"expected the band, got {inside}");
        Assert.Equal(SKColors.DimGray.Red, outside.Red);

        using var font = new SKFont(SKTypeface.Default, 40);
        var lines = ImageComposer.Wrap(font, "one two three four five six seven eight", 200);
        Assert.True(lines.Count > 1, "long text wraps");
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    /// <summary>
    /// An image layer (the manual-thumbnail path): a box naming a brand asset draws that
    /// picture instead of text, fitted inside the box and centred, so dragging a corner in
    /// the editor scales the image rather than distorting it. The field existed on the DTO
    /// for a long time with nothing reading it.
    /// </summary>
    [Fact]
    public void An_image_layer_is_drawn_into_its_box_fitted_and_centred()
    {
        var composer = new ImageComposer(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageComposer>.Instance);
        var layerId = Guid.NewGuid();
        // A SQUARE layer inside a WIDE box: fitting must pillarbox it, not stretch it.
        var spec = new OverlaySpec([
            new OverlayBox("layer", string.Empty, 0.25, 0.25, 0.5, 0.5, LogoAssetId: layerId),
        ]);

        var result = composer.ComposeOverlay(
            Png(1000, 1000, SKColors.DimGray),
            spec,
            new Dictionary<Guid, byte[]> { [layerId] = Png(200, 200, SKColors.Red) });

        using var bitmap = ImageReferenceResolver.TryDecode(result.Image)!;
        // Centre of the box is the layer.
        var centre = bitmap.GetPixel(500, 500);
        Assert.True(centre.Red > 200 && centre.Green < 60, $"expected the layer at the centre, got {centre}");
        // Outside the box the base image is untouched.
        var outside = bitmap.GetPixel(60, 60);
        Assert.Equal(SKColors.DimGray.Red, outside.Red);
    }

    [Fact]
    public void An_image_layer_whose_bytes_are_missing_is_skipped_rather_than_failing()
    {
        var composer = new ImageComposer(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageComposer>.Instance);
        var spec = new OverlaySpec([
            new OverlayBox("layer", string.Empty, 0.1, 0.1, 0.5, 0.5, LogoAssetId: Guid.NewGuid()),
        ]);

        // A deleted asset must cost the producer one picture, not the whole thumbnail.
        var result = composer.ComposeOverlay(Png(400, 400, SKColors.DimGray), spec, layerImages: null);

        using var bitmap = ImageReferenceResolver.TryDecode(result.Image)!;
        Assert.Equal(SKColors.DimGray.Red, bitmap.GetPixel(200, 200).Red);
    }

    [Fact]
    public void An_image_layer_can_crop_to_its_focus_and_clip_to_a_circle()
    {
        var composer = new ImageComposer(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageComposer>.Instance);
        var layerId = Guid.NewGuid();
        var spec = new OverlaySpec([
            new OverlayBox(
                "portrait", string.Empty, 0.25, 0.25, 0.5, 0.5,
                LogoAssetId: layerId,
                Crop: new OverlayImageCrop(FocusX: 1, FocusY: 0.5, Zoom: 1),
                Shape: "circle"),
        ]);

        var result = composer.ComposeOverlay(
            Png(400, 400, SKColors.DimGray),
            spec,
            new Dictionary<Guid, byte[]>
            {
                [layerId] = SplitPng(200, 100, SKColors.Red, SKColors.Blue),
            });

        using var bitmap = ImageReferenceResolver.TryDecode(result.Image)!;
        var centre = bitmap.GetPixel(200, 200);
        Assert.True(centre.Blue > 200 && centre.Red < 60,
            $"expected the crop to focus the blue half, got {centre}");

        // Inside the square frame but outside its circular mask remains the base image.
        var clippedCorner = bitmap.GetPixel(110, 110);
        Assert.Equal(SKColors.DimGray.Red, clippedCorner.Red);
        Assert.Equal(SKColors.DimGray.Green, clippedCorner.Green);
        Assert.Equal(SKColors.DimGray.Blue, clippedCorner.Blue);
    }

    [Fact]
    public void Mask_helpers_find_the_edit_region_and_convert_to_the_alpha_convention()
    {
        using var mask = new SKBitmap(100, 50);
        mask.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(mask))
        {
            using var white = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(50, 10, 40, 20, white);
        }
        using var image = SKImage.FromBitmap(mask);
        var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        var bounds = RegionEdits.MaskBounds(png)!.Value;
        Assert.InRange(bounds.Left, 0.49f, 0.51f);
        Assert.InRange(bounds.Top, 0.19f, 0.21f);
        Assert.InRange(bounds.Right, 0.89f, 0.91f);
        Assert.InRange(bounds.Bottom, 0.59f, 0.61f);

        var described = RegionEdits.DescribeRegion("replace the background", png);
        Assert.Contains("50%–90% of the width", described, StringComparison.Ordinal);

        using var alpha = ImageReferenceResolver.TryDecode(RegionEdits.ToAlphaMask(png, 200, 100))!;
        Assert.Equal((200, 100), (alpha.Width, alpha.Height));
        Assert.Equal(0, alpha.GetPixel(140, 40).Alpha);     // inside the region: transparent = repaint
        Assert.Equal(255, alpha.GetPixel(10, 10).Alpha);    // outside: opaque = keep

        Assert.Null(RegionEdits.MaskBounds(Png(10, 10, SKColors.Black)));
    }

    [Fact]
    public void Technical_briefs_round_trip_and_ride_on_the_brief_string()
    {
        var brief = new TechnicalBrief("Ignite UI for React", "24.2", "IgrGrid, IgrCombo", "No SSR for charts", null, "Vue support");
        var json = TechnicalBriefs.Serialize(brief)!;
        Assert.Equal(brief, TechnicalBriefs.Parse(json));
        Assert.Null(TechnicalBriefs.Serialize(new TechnicalBrief()));
        Assert.Null(TechnicalBriefs.Parse("not json"));

        var merged = TechnicalBriefs.Merge("Tighter intro.", brief)!;
        Assert.StartsWith("Tighter intro.", merged, StringComparison.Ordinal);
        Assert.Contains("TECHNICAL BRIEF", merged, StringComparison.Ordinal);
        Assert.Contains("Must NOT claim: Vue support", merged, StringComparison.Ordinal);
        Assert.Equal("Tighter intro.", TechnicalBriefs.Merge("Tighter intro.", null));
        Assert.Equal("Ignite UI for React · 24.2 · IgrGrid, IgrCombo", TechnicalBriefs.QuerySeed(brief));
    }

    [Fact]
    public void Claims_are_verified_only_by_a_real_http_source()
    {
        using var doc = JsonDocument.Parse("""
            {"claims":[
              {"statement":"IgrGrid virtualises rows","sourceUrl":"https://docs.example/grid"},
              {"statement":"Ships a Vue adapter","sourceUrl":null},
              {"statement":"Uses javascript: tricks","sourceUrl":"javascript:alert(1)"},
              {"bogus":true}
            ]}
            """);
        var claims = AiOrchestrator.ReadClaims(doc.RootElement);

        Assert.Equal(3, claims.Count);
        Assert.True(claims[0].Verified);
        Assert.Equal("https://docs.example/grid", claims[0].SourceUrl);
        Assert.False(claims[1].Verified);
        Assert.False(claims[2].Verified);
        Assert.Null(claims[2].SourceUrl);
        Assert.Empty(AiOrchestrator.ReadClaims(JsonDocument.Parse("{}").RootElement));
    }
}
