using Castmill.Core.Resources;
using Castmill.UI.Pages.Campaign.Bench;

namespace Castmill.UI.Tests;

/// <summary>
/// The preview half of the composite contract (ADR-082/F72). ImageComposer draws the same
/// numbers; if one of these changes, OverlayLayerCompositionTests must change with it.
/// </summary>
public sealed class BenchLayersTests
{
    [Fact]
    public void Cover_centres_the_source_window_on_the_focus_and_keeps_it_inside_the_picture()
    {
        // 200×100 picture in a 100×100 px frame (slot 1000×1000, box 0.1): scale 1, window 100×100.
        var box = new OverlayBox("a", string.Empty, 0, 0, 0.1, 0.1, Crop: new OverlayImageCrop(FocusX: 1, FocusY: 0.5, Zoom: 1));
        var g = BenchLayers.Cover(box, 1000, 1000, 200, 100);

        Assert.Equal(100, g.SourceWidth, 6);
        Assert.Equal(100, g.SourceHeight, 6);
        Assert.Equal(100, g.SourceLeft, 6);
        Assert.Equal(0, g.SourceTop, 6);

        var zoomed = BenchLayers.Cover(box with { Crop = new OverlayImageCrop(0.5, 0.5, 2) }, 1000, 1000, 200, 100);
        Assert.Equal(50, zoomed.SourceWidth, 6);
        Assert.Equal(75, zoomed.SourceLeft, 6);
        Assert.Equal(25, zoomed.SourceTop, 6);
    }

    [Fact]
    public void The_picture_is_positioned_so_the_frame_shows_exactly_the_source_window()
    {
        var box = new OverlayBox("a", string.Empty, 0, 0, 0.1, 0.1, Crop: new OverlayImageCrop(FocusX: 1, FocusY: 0.5, Zoom: 1));

        var style = BenchLayers.ImageStyle(box, 1000, 1000, (200, 100));

        Assert.Equal("left:-100%;top:0%;width:200%;height:100%;", style);
        Assert.Contains("object-fit:cover", BenchLayers.ImageStyle(box, 1000, 1000, null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lift", "drop-shadow(0cqh 0.56cqh 0.835cqh rgba(0,0,0,0.38))")]
    [InlineData("float", "drop-shadow(0cqh 3.3cqh 2.75cqh rgba(0,0,0,0.55))")]
    [InlineData("sticker", "drop-shadow(0cqh 1.67cqh 1.65cqh rgba(0,0,0,0.45))")]
    [InlineData("bevel", "drop-shadow(0cqh 1.67cqh 1.8cqh rgba(0,0,0,0.5))")]
    [InlineData("glow", "drop-shadow(0cqh 0cqh 2.35cqh rgba(255,209,102,0.85))")]
    public void Each_effect_preset_is_one_drop_shadow_in_slot_height_units_at_the_composers_softness(string preset, string expected)
    {
        Assert.Equal(expected, BenchLayers.ShadowFilter(new OverlayEffect(preset)));
    }

    [Fact]
    public void Depth_scales_the_shadow_and_none_draws_nothing()
    {
        Assert.Equal("drop-shadow(0cqh 1.12cqh 1.67cqh rgba(0,0,0,0.38))", BenchLayers.ShadowFilter(new OverlayEffect("lift", 1)));
        Assert.Null(BenchLayers.ShadowFilter(new OverlayEffect()));
        Assert.DoesNotContain("filter", BenchLayers.LayerStyle(new OverlayBox("a", "x", 0, 0, 0.5, 0.5)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sticker_gets_a_default_white_border_and_bevel_adds_light_and_shade_edges()
    {
        var sticker = new OverlayBox("a", string.Empty, 0, 0, 0.5, 0.5, LogoAssetId: Guid.NewGuid(), Effect: new OverlayEffect("sticker"));
        Assert.Equal("box-shadow:inset 0 0 0 1.39cqh #FFFFFF;", BenchLayers.EdgeStyle(sticker));

        var bevel = sticker with { Effect = new OverlayEffect("bevel", BorderWidth: 0.01, BorderColor: "#112233") };
        var edges = BenchLayers.EdgeStyle(bevel);
        Assert.Contains("inset 0 0 0 1cqh #112233", edges, StringComparison.Ordinal);
        Assert.Contains("inset 0.56cqh 0.56cqh 0 rgba(255,255,255,0.45)", edges, StringComparison.Ordinal);
        Assert.Contains("inset -0.7cqh -0.7cqh 0 rgba(0,0,0,0.38)", edges, StringComparison.Ordinal);
    }

    [Fact]
    public void Circle_and_square_snap_to_a_physical_square_and_original_restores_the_aspect()
    {
        var photo = new OverlayBox("a", string.Empty, 0.1, 0.1, 0.5, 0.5, LogoAssetId: Guid.NewGuid(), Crop: new OverlayImageCrop(), Kind: "image");

        var circle = BenchLayers.WithShape(photo, "circle", 1280, 720, (1500, 1000));
        Assert.Equal(circle.W * 1280, circle.H * 720, 6);
        Assert.Equal(360, circle.H * 720, 6);
        Assert.Equal(photo.X + (photo.W / 2), circle.X + (circle.W / 2), 6);

        var back = BenchLayers.WithShape(circle, "rectangle", 1280, 720, (1500, 1000));
        Assert.Equal(1.5, back.W * 1280 / (back.H * 720), 6);

        var rounded = BenchLayers.WithShape(photo, "rounded", 1280, 720, null);
        Assert.True(rounded.CornerRadius > 0);
    }

    [Fact]
    public void Fit_to_image_contains_the_picture_inside_its_old_frame()
    {
        var box = new OverlayBox("a", string.Empty, 0.25, 0.25, 0.5, 0.5, LogoAssetId: Guid.NewGuid());

        var fitted = BenchLayers.FitToImage(box, 1280, 720, 2000, 1000);

        Assert.Equal(640, fitted.W * 1280, 6);
        Assert.Equal(320, fitted.H * 720, 6);
        Assert.Equal(0.25 + (20d / 720), fitted.Y, 6);
    }

    [Fact]
    public void Legacy_boxes_upgrade_to_layers_keeping_their_text_and_geometry()
    {
        var text = BenchLayers.Upgrade(new OverlayBox("t", "Launch day", 0.1, 0.2, 0.3, 0.4));
        Assert.Equal("text", text.Kind);
        Assert.Null(text.Name);
        Assert.Equal(OverlayFonts.Default, text.FontFamily);
        Assert.Equal((0.1, 0.2, 0.3, 0.4), (text.X, text.Y, text.W, text.H));

        var legacyPicture = new OverlayBox("p", string.Empty, 0, 0, 0.5, 0.5, LogoAssetId: Guid.NewGuid());
        Assert.True(BenchLayers.NeedsFitOnMeasure(legacyPicture));
        var picture = BenchLayers.Upgrade(legacyPicture);
        Assert.Equal("image", picture.Kind);
        Assert.NotNull(picture.Crop);
        Assert.Same(picture, BenchLayers.Upgrade(picture));
    }

    [Fact]
    public void Text_presets_scale_with_the_slot_height_and_style_in_container_units()
    {
        var headline = BenchLayers.TextPresets.Single(p => p.Key == "headline");

        var layer = BenchLayers.NewText(headline, 0.5, 0.5, 1600, 840);

        Assert.Equal(96d / 720, layer.FontSize, 6);
        Assert.Equal(230d / 720, layer.H, 6);
        Assert.Equal(0.5, layer.X + (layer.W / 2), 6);
        var style = BenchLayers.TextStyle(layer);
        Assert.Contains("font-size:13.3333cqh", style, StringComparison.Ordinal);
        Assert.Contains("line-height:1.1", style, StringComparison.Ordinal);
        Assert.Contains("padding:2.3333cqh 4.6667cqh", style, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_square_slot_presets_scale_by_the_tighter_side_and_land_inside_the_canvas()
    {
        var headline = BenchLayers.TextPresets.Single(p => p.Key == "headline");

        var layer = BenchLayers.NewText(headline, 0.05, 0.5, 1200, 1200);

        // Width-bound: 1200/1280 of the thumbnail design, so 620 px becomes 581.25 px.
        Assert.Equal(581.25, layer.W * 1200, 6);
        Assert.Equal(96 * 1200d / 1280, layer.FontSize * 1200, 6);
        Assert.Equal(0, layer.X, 6);
    }

    [Fact]
    public void A_dropped_picture_is_pulled_back_onto_the_canvas()
    {
        var picture = BenchLayers.NewImage(Guid.NewGuid(), "p", 0.98, 0.02, 1280, 720, 1.5);

        Assert.Equal(1 - picture.W, picture.X, 6);
        Assert.Equal(0, picture.Y, 6);
    }

    [Fact]
    public void Colours_convert_with_opacity_and_bad_input_falls_back_to_white()
    {
        Assert.Equal("rgba(51,102,153,0.35)", BenchLayers.Rgba("#336699", 0.35));
        Assert.Equal("rgba(0,0,0,0.502)", BenchLayers.Rgba("#00000080", 1));
        Assert.Equal("rgba(255,255,255,1)", BenchLayers.Rgba("teal", 1));
        Assert.Equal("#336699", BenchLayers.ColorInputValue("#336699CC"));
    }

    [Theory]
    [InlineData("Anton", 800, 400)]
    [InlineData("IBM Plex Mono", 800, 700)]
    [InlineData("Barlow", 500, 600)]
    [InlineData(null, 800, 800)]
    public void Weights_clamp_to_what_the_face_ships(string? family, int requested, int expected)
    {
        Assert.Equal(expected, BenchLayers.NearestWeight(family, requested));
    }
}
