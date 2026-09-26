using System.Globalization;
using Castmill.Core.Resources;

namespace Castmill.UI.Pages.Campaign.Bench;

/// <summary>A starting text layer the tray offers. Sizes are output pixels at a 720 px-tall slot.</summary>
public sealed record BenchTextPreset(
    string Key, string Name, string Sample, string Text, string Font, double SizePx, int Weight,
    string Color, string BandColor, double BandOpacity, double WidthPx, double HeightPx,
    string Align, double RadiusPx);

/// <summary>
/// The image editor's layer maths (ADR-082 / ADR-F72), kept out of markup so it is testable.
/// Every number that decides how a layer LOOKS is mirrored exactly by ImageComposer's v2 path:
/// cover-crop source rect, drop-shadow presets, inset border, bevel edges and text padding are
/// all ratios of the slot height, written here as container-query units (1cqh = 1% of height).
/// </summary>
public static class BenchLayers
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Line height the composer uses for v2 text, as a multiple of the font size.</summary>
    public const double LineHeight = 1.1;

    /// <summary>Text padding when a layer carries no band, as a ratio of the font size.</summary>
    public const double DefaultPadding = 0.35;

    /// <summary>The border a Sticker draws when the producer has not chosen one (ratio of height).</summary>
    public const double StickerBorder = 0.0139;

    public static readonly IReadOnlyList<BenchTextPreset> TextPresets =
    [
        new("headline", "Headline", "HEAD", "YOUR HEADLINE\nGOES HERE", "Barlow Condensed", 96, 800, "#FFFFFF", "#000000", 0, 620, 230, "left", 0),
        new("subhead", "Subhead", "Sub", "A short supporting line", "Barlow", 40, 600, "#F2F2F3", "#1D2D3D", 0.85, 480, 72, "left", 0),
        new("label", "Label chip", "NEW", "NEW", "Barlow Condensed", 44, 800, "#1D1F20", "#FFD166", 1, 150, 70, "center", 6),
        new("quote", "Serif quote", "“Aa”", "“It just works.”", "DM Serif Display", 60, 400, "#FFFFFF", "#000000", 0.45, 520, 110, "left", 0),
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> Shapes =
        [("rectangle", "Original"), ("square", "Square"), ("circle", "Circle"), ("rounded", "Rounded")];

    public static readonly IReadOnlyList<(string Value, string Label)> Effects =
        [("none", "None"), ("lift", "Lift"), ("float", "Float"), ("sticker", "Sticker"), ("bevel", "Bevel"), ("glow", "Glow")];

    public static readonly IReadOnlyList<(int Value, string Label)> Weights =
        [(400, "Regular"), (600, "Semibold"), (700, "Bold"), (800, "Black")];

    /// <summary>
    /// Only the weights each face really ships. The composer emboldens synthetically when a
    /// weight is missing and browsers synthesise differently, so offering a weight a face does
    /// not have would make the preview and the published image disagree.
    /// </summary>
    public static IReadOnlyList<int> WeightsFor(string? family) => (family ?? OverlayFonts.Default) switch
    {
        "Anton" or "DM Serif Display" => [400],
        "IBM Plex Mono" => [400, 600, 700],
        _ => [400, 600, 700, 800],
    };

    /// <summary>The nearest weight the face ships, preferring the heavier on a tie.</summary>
    public static int NearestWeight(string? family, int weight) =>
        WeightsFor(family).OrderBy(w => Math.Abs(w - weight)).ThenByDescending(w => w).First();

    public static string NewId() => $"layer-{Guid.NewGuid():N}"[..14];

    public static bool IsImage(OverlayBox box) => box.LogoAssetId is not null;

    /// <summary>Square and circle frames are physically square: equal PIXELS, not equal ratios.</summary>
    public static bool IsFixedAspect(OverlayBox box) =>
        IsImage(box) && box.Shape is "square" or "circle";

    public static OverlayEffect EffectOf(OverlayBox box) => box.Effect ?? new OverlayEffect();

    /// <summary>
    /// Opens a legacy ADR-055 box in the layer model so the editor can style it. Geometry and
    /// text survive; an uncropped legacy picture keeps its whole-image look because the editor
    /// shrinks its frame to the image's aspect once the picture has been measured.
    /// </summary>
    public static OverlayBox Upgrade(OverlayBox box) => box.Kind is not null
        ? box
        : box with
        {
            Kind = IsImage(box) ? "image" : "text",
            Band = IsImage(box) ? box.Band : box.Band ?? new OverlayBand("#000000", 0, DefaultPadding, 0),
            Crop = IsImage(box) ? box.Crop ?? new OverlayImageCrop() : box.Crop,
            FontFamily = IsImage(box) ? box.FontFamily : box.FontFamily ?? OverlayFonts.Default,
        };

    /// <summary>A legacy uncropped rectangle picture: the one upgrade that needs a measured aspect.</summary>
    public static bool NeedsFitOnMeasure(OverlayBox legacy) =>
        legacy.Kind is null && IsImage(legacy) && legacy.Crop is null && legacy.Shape is not ("square" or "circle");

    public static OverlayBox NewImage(
        Guid brandAssetId, string name, double centreX, double centreY, int slotWidth, int slotHeight, double? imageAspect)
    {
        var aspect = imageAspect is > 0 ? imageAspect.Value : 1.5;
        // Largest comfortable size: 38% of the width, never taller than 62% of the height.
        var widthPx = slotWidth * 0.38;
        var heightPx = widthPx / aspect;
        if (heightPx > slotHeight * 0.62)
        {
            heightPx = slotHeight * 0.62;
            widthPx = heightPx * aspect;
        }
        var w = widthPx / slotWidth;
        var h = heightPx / slotHeight;
        var (x, y) = PlaceInside(centreX, centreY, w, h);
        return new OverlayBox(
            NewId(), string.Empty, x, y, w, h,
            LogoAssetId: brandAssetId, Crop: new OverlayImageCrop(), Kind: "image",
            Name: Truncate(name, 60));
    }

    /// <summary>
    /// Presets are drawn for a 1280×720 thumbnail. Other slots scale by the tighter side, so a
    /// headline on a 1200×1200 social card is as wide relative to the card as it is on a
    /// thumbnail rather than spilling off it.
    /// </summary>
    public static OverlayBox NewText(BenchTextPreset preset, double centreX, double centreY, int slotWidth, int slotHeight)
    {
        var scale = Math.Min(slotWidth / 1280d, slotHeight / 720d);
        var w = Math.Min(1, preset.WidthPx * scale / slotWidth);
        var h = Math.Min(1, preset.HeightPx * scale / slotHeight);
        var (x, y) = PlaceInside(centreX, centreY, w, h);
        return new OverlayBox(
            NewId(), preset.Text, x, y, w, h,
            FontSize: Math.Clamp(preset.SizePx * scale / slotHeight, 0.01, 0.6), Weight: preset.Weight, Color: preset.Color, Align: preset.Align,
            Band: new OverlayBand(preset.BandColor, preset.BandOpacity, DefaultPadding, 0),
            Kind: "text", FontFamily: preset.Font,
            CornerRadius: preset.RadiusPx * scale / slotHeight);
    }

    /// <summary>A new layer lands centred on the drop point but never hanging off the canvas.</summary>
    public static (double X, double Y) PlaceInside(double centreX, double centreY, double w, double h) =>
        (Math.Clamp(centreX - (w / 2), 0, Math.Max(0, 1 - w)), Math.Clamp(centreY - (h / 2), 0, Math.Max(0, 1 - h)));

    /// <summary>
    /// Changing an image's frame shape. Square/circle snap to the largest physical square that
    /// fits the current frame, centred; going back to Original restores the picture's aspect
    /// when it is known and the crop is untouched, so "undo the circle" gives the photo back.
    /// </summary>
    public static OverlayBox WithShape(OverlayBox box, string shape, int slotWidth, int slotHeight, (int Width, int Height)? natural)
    {
        var next = box with { Shape = shape };
        var widthPx = box.W * slotWidth;
        var heightPx = box.H * slotHeight;
        if (shape is "square" or "circle")
        {
            var side = Math.Min(widthPx, heightPx);
            next = next with
            {
                X = box.X + ((widthPx - side) / 2 / slotWidth),
                Y = box.Y + ((heightPx - side) / 2 / slotHeight),
                W = side / slotWidth,
                H = side / slotHeight,
            };
        }
        else if (box.Shape is "square" or "circle" && natural is { Width: > 0, Height: > 0 } n
                 && (box.Crop ?? new OverlayImageCrop()).Zoom <= 1.001)
        {
            var aspect = (double)n.Width / n.Height;
            var newWidthPx = heightPx * aspect;
            next = next with
            {
                X = box.X + ((widthPx - newWidthPx) / 2 / slotWidth),
                W = newWidthPx / slotWidth,
            };
        }
        if (shape == "rounded" && box.CornerRadius <= 0)
        {
            next = next with { CornerRadius = 24d / 720d };
        }
        return next;
    }

    /// <summary>Shrinks a frame to the picture's own aspect inside its current bounds (contain), centred.</summary>
    public static OverlayBox FitToImage(OverlayBox box, int slotWidth, int slotHeight, int naturalWidth, int naturalHeight)
    {
        var widthPx = box.W * slotWidth;
        var heightPx = box.H * slotHeight;
        var scale = Math.Min(widthPx / naturalWidth, heightPx / naturalHeight);
        var fitWidth = naturalWidth * scale;
        var fitHeight = naturalHeight * scale;
        return box with
        {
            X = box.X + ((widthPx - fitWidth) / 2 / slotWidth),
            Y = box.Y + ((heightPx - fitHeight) / 2 / slotHeight),
            W = fitWidth / slotWidth,
            H = fitHeight / slotHeight,
        };
    }

    /// <summary>
    /// The composer's crop: cover the frame, apply zoom, centre the source window on the focus
    /// and keep it inside the picture. Returns the source window in source pixels and the
    /// drawn picture size relative to the frame.
    /// </summary>
    public static CoverGeometry Cover(OverlayBox box, int slotWidth, int slotHeight, int naturalWidth, int naturalHeight)
    {
        var crop = box.Crop ?? new OverlayImageCrop();
        var frameWidth = box.W * slotWidth;
        var frameHeight = box.H * slotHeight;
        var zoom = Math.Clamp(crop.Zoom, 1, 4);
        var scale = Math.Max(frameWidth / naturalWidth, frameHeight / naturalHeight) * zoom;
        var sourceWidth = Math.Min(naturalWidth, frameWidth / scale);
        var sourceHeight = Math.Min(naturalHeight, frameHeight / scale);
        var sourceLeft = Math.Clamp((Math.Clamp(crop.FocusX, 0, 1) * naturalWidth) - (sourceWidth / 2), 0, naturalWidth - sourceWidth);
        var sourceTop = Math.Clamp((Math.Clamp(crop.FocusY, 0, 1) * naturalHeight) - (sourceHeight / 2), 0, naturalHeight - sourceHeight);
        return new CoverGeometry(sourceLeft, sourceTop, sourceWidth, sourceHeight, scale, frameWidth, frameHeight, naturalWidth, naturalHeight);
    }

    // ---- CSS: the preview half of the composite contract ---------------------------------

    public static string LayerStyle(OverlayBox box, bool suppressEffect = false)
    {
        var style = $"left:{Pct(box.X)};top:{Pct(box.Y)};width:{Pct(box.W)};height:{Pct(box.H)};";
        if (box.Opacity < 1)
        {
            style += $"opacity:{Num(box.Opacity)};";
        }
        if (!suppressEffect && ShadowFilter(EffectOf(box)) is { } filter)
        {
            style += $"filter:{filter};";
        }
        return style;
    }

    /// <summary>
    /// One CSS drop-shadow per preset. The preset blur is a box-shadow-style radius and the
    /// composer draws it with sigma = blur / 2, but CSS drop-shadow() takes the Gaussian's
    /// standard deviation directly (measured in Chromium: 20px drop-shadow ≈ 40px box-shadow),
    /// so the preview writes blur / 2 to paint the same softness.
    /// </summary>
    public static string? ShadowFilter(OverlayEffect effect)
    {
        var d = Math.Clamp(effect.Depth, 0, 1) * 2;
        return effect.Preset switch
        {
            "lift" => Shadow(0, 0.56 * d, 1.67 * d, "rgba(0,0,0,0.38)"),
            "float" => Shadow(0, 3.3 * d, 5.5 * d, "rgba(0,0,0,0.55)"),
            "sticker" => Shadow(0, 1.67 * d, 3.3 * d, "rgba(0,0,0,0.45)"),
            "bevel" => Shadow(0, 1.67 * d, 3.6 * d, "rgba(0,0,0,0.5)"),
            "glow" => Shadow(0, 0, 4.7 * d, Rgba(GlowColor(effect), 0.85)),
            _ => null,
        };

        static string Shadow(double x, double y, double blur, string colour) =>
            $"drop-shadow({Num(x)}cqh {Num(y)}cqh {Num(blur / 2)}cqh {colour})";
    }

    public static string GlowColor(OverlayEffect effect) =>
        string.Equals(effect.BorderColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase) ? "#FFD166" : effect.BorderColor;

    public static double BorderWidthOf(OverlayEffect effect) =>
        effect.Preset == "sticker" && effect.BorderWidth <= 0 ? StickerBorder : effect.BorderWidth;

    public static string FrameStyle(OverlayBox box)
    {
        var radius = RadiusCss(box);
        var style = radius is null ? string.Empty : $"border-radius:{radius};";
        if (!IsImage(box) && box.Band is { Opacity: > 0 } band)
        {
            style += $"background:{Rgba(band.Color, band.Opacity)};";
        }
        return style;
    }

    public static string? RadiusCss(OverlayBox box) =>
        IsImage(box) && box.Shape == "circle" ? "50%"
        : (!IsImage(box) || box.Shape == "rounded") && box.CornerRadius > 0 ? $"{Num(box.CornerRadius * 100)}cqh"
        : null;

    /// <summary>Inset border then bevel edges, painted above the content like the composer does.</summary>
    public static string EdgeStyle(OverlayBox box)
    {
        var effect = EffectOf(box);
        var shadows = new List<string>();
        var border = BorderWidthOf(effect);
        if (border > 0)
        {
            shadows.Add($"inset 0 0 0 {Num(border * 100)}cqh {effect.BorderColor}");
        }
        if (effect.Preset == "bevel")
        {
            var d = Math.Clamp(effect.Depth, 0, 1) * 2;
            shadows.Add($"inset {Num(0.56 * d)}cqh {Num(0.56 * d)}cqh 0 rgba(255,255,255,0.45)");
            shadows.Add($"inset {Num(-0.7 * d)}cqh {Num(-0.7 * d)}cqh 0 rgba(0,0,0,0.38)");
        }
        var radius = RadiusCss(box);
        return (radius is null ? string.Empty : $"border-radius:{radius};")
            + (shadows.Count == 0 ? string.Empty : $"box-shadow:{string.Join(',', shadows)};");
    }

    /// <summary>The picture inside its frame, positioned so the frame shows exactly the composer's source window.</summary>
    public static string ImageStyle(OverlayBox box, int slotWidth, int slotHeight, (int Width, int Height)? natural)
    {
        if (natural is not { Width: > 0, Height: > 0 } n)
        {
            return "left:0;top:0;width:100%;height:100%;object-fit:cover;";
        }
        var g = Cover(box, slotWidth, slotHeight, n.Width, n.Height);
        var drawnWidth = g.NaturalWidth * g.Scale;
        var drawnHeight = g.NaturalHeight * g.Scale;
        return $"left:{Pct(-g.SourceLeft * g.Scale / g.FrameWidth)};top:{Pct(-g.SourceTop * g.Scale / g.FrameHeight)};"
            + $"width:{Pct(drawnWidth / g.FrameWidth)};height:{Pct(drawnHeight / g.FrameHeight)};";
    }

    public static string TextStyle(OverlayBox box)
    {
        var padX = (box.Band?.Padding ?? DefaultPadding) * box.FontSize * 100;
        var family = box.FontFamily ?? OverlayFonts.Default;
        return $"font-family:\"{family}\",var(--cm-font-body);font-size:{Num(box.FontSize * 100)}cqh;font-weight:{box.Weight};"
            + $"line-height:{Num(LineHeight)};color:{Rgba(box.Color, box.TextOpacity)};text-align:{box.Align};"
            + $"padding:{Num(padX / 2)}cqh {Num(padX)}cqh;";
    }

    // ---- Units ---------------------------------------------------------------------------

    public static double ToPx(double ratio, int size) => Math.Round(ratio * size, 1);

    public static double FromPx(double px, int size) => size <= 0 ? 0 : px / size;

    public static string Pct(double ratio) => Num(ratio * 100) + "%";

    public static string Num(double value) => Clean(value).ToString("0.####", Inv);

    /// <summary>Full precision for the data-* geometry the canvas island reads back and commits.</summary>
    public static string Exact(double value) => Clean(value).ToString("0.##########", Inv);

    /// <summary>No "-0": a negated zero offset is still zero.</summary>
    private static double Clean(double value) => Math.Abs(value) < 1e-12 ? 0 : value;

    /// <summary>"#RRGGBB" (or #RRGGBBAA) at an extra opacity → rgba(); unparseable colours fall back to white.</summary>
    public static string Rgba(string? hex, double opacity)
    {
        var (r, g, b, a) = ParseHex(hex);
        return $"rgba({r},{g},{b},{Num(Math.Clamp(a * opacity, 0, 1))})";
    }

    public static (int R, int G, int B, double A) ParseHex(string? hex)
    {
        var value = (hex ?? string.Empty).TrimStart('#');
        if (value.Length is 6 or 8 && int.TryParse(value[..6], NumberStyles.HexNumber, Inv, out var rgb))
        {
            var alpha = value.Length == 8 && int.TryParse(value[6..], NumberStyles.HexNumber, Inv, out var aa) ? aa / 255d : 1d;
            return ((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255, alpha);
        }
        return (255, 255, 255, 1);
    }

    /// <summary>Colour inputs speak #rrggbb only; an 8-digit or malformed value is shown as its RGB part.</summary>
    public static string ColorInputValue(string? hex)
    {
        var (r, g, b, _) = ParseHex(hex);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    public static string FirstLine(string? text)
    {
        var line = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n')[0].Trim();
        return line.Length == 0 ? "Text" : Truncate(line, 60);
    }

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>The cover-crop solution for one image layer, in source pixels and output pixels.</summary>
public sealed record CoverGeometry(
    double SourceLeft, double SourceTop, double SourceWidth, double SourceHeight, double Scale,
    double FrameWidth, double FrameHeight, int NaturalWidth, int NaturalHeight)
{
    /// <summary>Source pixels behind each output pixel; below 1 the picture is being upscaled and will look soft.</summary>
    public double SourcePixelsPerOutputPixel => FrameWidth <= 0 ? 0 : SourceWidth / FrameWidth;
}
