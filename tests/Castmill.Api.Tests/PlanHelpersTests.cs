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
