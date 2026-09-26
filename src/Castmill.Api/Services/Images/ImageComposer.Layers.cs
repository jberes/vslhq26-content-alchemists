using Castmill.Core.Resources;
using SkiaSharp;

namespace Castmill.Api.Services.Images;

/// <summary>
/// The ADR-082 layer model: boxes with a non-null Kind. Every number here is a ratio of the
/// slot height and is mirrored by the editor's CSS, so a change on one side must be made on
/// the other or the preview stops matching the composite.
/// </summary>
public sealed partial class ImageComposer
{
    internal const float StickerDefaultBorder = 0.0139f;
    internal const float BevelHighlightOffset = 0.0056f;
    internal const float BevelShadeOffset = 0.007f;
    internal const float TextLineHeight = 1.1f;
    internal const float DefaultTextPadding = 0.35f;
    internal const float MinimumShrinkSize = 9f;

    private static readonly SKColor DefaultTextColor = new(0xF2, 0xF2, 0xF3);
    private static readonly SKColor GlowForWhite = new(0xFF, 0xD1, 0x66);

    /// <summary>A preset's drop shadow in pixels. Blur is the CSS blur radius; Skia's sigma is half of it.</summary>
    internal readonly record struct LayerShadow(float Dx, float Dy, float Blur, SKColor Color);

    internal readonly record struct TextLayout(
        IReadOnlyList<string> Lines, float Size, float PadX, float PadY, float LineHeight, float Top);

    internal static LayerShadow? ShadowFor(OverlayEffect? effect, float height)
    {
        if (effect is null)
        {
            return null;
        }
        var scale = height * (float)(Math.Clamp(effect.Depth, 0, 1) * 2);
        return effect.Preset switch
        {
            "lift" => new LayerShadow(0, 0.0056f * scale, 0.0167f * scale, SKColors.Black.WithAlpha(Alpha(0.38))),
            "float" => new LayerShadow(0, 0.033f * scale, 0.055f * scale, SKColors.Black.WithAlpha(Alpha(0.55))),
            "sticker" => new LayerShadow(0, 0.0167f * scale, 0.033f * scale, SKColors.Black.WithAlpha(Alpha(0.45))),
            "bevel" => new LayerShadow(0, 0.0167f * scale, 0.036f * scale, SKColors.Black.WithAlpha(Alpha(0.50))),
            "glow" => new LayerShadow(0, 0, 0.047f * scale, GlowColor(effect.BorderColor).WithAlpha(Alpha(0.85))),
            _ => null,
        };
    }

    /// <summary>White glows are invisible on light art, so a white border glows warm yellow instead.</summary>
    internal static SKColor GlowColor(string? borderColor)
    {
        var color = ParseColor(borderColor) ?? SKColors.White;
        return color.Red == 0xFF && color.Green == 0xFF && color.Blue == 0xFF ? GlowForWhite : color;
    }

    /// <summary>Inset border width in pixels; a sticker with no explicit border gets its die-cut edge.</summary>
    internal static float BorderWidthFor(OverlayEffect? effect, float height)
    {
        if (effect is null)
        {
            return 0;
        }
        var ratio = effect.BorderWidth > 0
            ? (float)effect.BorderWidth
            : effect.Preset == "sticker" ? StickerDefaultBorder : 0;
        return ratio * height;
    }

    /// <summary>The layer's mask. Text layers are a (rounded) rectangle; the Shape field is an image-frame control.</summary>
    internal static SKPath LayerShape(OverlayBox box, SKRect frame, float height)
    {
        var radius = (float)(box.CornerRadius * height);
        var shape = box.Kind == "text" ? (radius > 0 ? "rounded" : "rectangle") : box.Shape;
        using var builder = new SKPathBuilder();
        switch (shape)
        {
            case "circle":
                builder.AddOval(frame);
                break;
            case "rounded" when radius > 0:
                builder.AddRoundRect(frame, radius, radius);
                break;
            default:
                builder.AddRect(frame);
                break;
        }
        return builder.Detach();
    }

    /// <summary>
    /// The source rectangle that covers a frame: scaled to cover, zoomed, centred on the focus
    /// and clamped inside the image. Shared by legacy cropped boxes and every v2 image layer.
    /// </summary>
    internal static SKRect CoverSource(int layerWidth, int layerHeight, float frameWidth, float frameHeight, OverlayImageCrop crop)
    {
        var zoom = (float)Math.Clamp(crop.Zoom, 1, 4);
        var coverScale = Math.Max(frameWidth / layerWidth, frameHeight / layerHeight) * zoom;
        var sourceWidth = Math.Min(layerWidth, frameWidth / coverScale);
        var sourceHeight = Math.Min(layerHeight, frameHeight / coverScale);
        var focusX = (float)Math.Clamp(crop.FocusX, 0, 1) * layerWidth;
        var focusY = (float)Math.Clamp(crop.FocusY, 0, 1) * layerHeight;
        var sourceLeft = Math.Clamp(focusX - (sourceWidth / 2f), 0, layerWidth - sourceWidth);
        var sourceTop = Math.Clamp(focusY - (sourceHeight / 2f), 0, layerHeight - sourceHeight);
        return SKRect.Create(sourceLeft, sourceTop, sourceWidth, sourceHeight);
    }

    /// <summary>
    /// Wraps to the padded box width and steps the size down until the block fits the padded
    /// box height (or reaches the floor). Leaves <paramref name="font"/> at the settled size.
    /// </summary>
    internal static TextLayout LayoutText(SKFont font, string text, SKRect frame, double? bandPadding)
    {
        var padRatio = (float)(bandPadding ?? DefaultTextPadding);
        while (true)
        {
            var size = font.Size;
            var padX = padRatio * size;
            var padY = padX * 0.5f;
            var lineHeight = TextLineHeight * size;
            var lines = Wrap(font, text, frame.Width - (2 * padX));
            if (lines.Count * lineHeight <= frame.Height - (2 * padY) || size <= MinimumShrinkSize)
            {
                var top = frame.Top + ((frame.Height - (lines.Count * lineHeight)) / 2f);
                return new TextLayout(lines, size, padX, padY, lineHeight, top);
            }
            font.Size = size - 1f;
        }
    }

    /// <summary>
    /// One v2 layer, drawn into its own canvas layer so opacity and the preset's drop shadow
    /// apply to exactly what the layer painted. Returns true when a text layer had to fall
    /// back to the legacy face.
    /// </summary>
    private static bool DrawLayer(
        SKCanvas canvas,
        OverlayBox box,
        int width,
        int height,
        SKTypeface legacyFace,
        IReadOnlyDictionary<Guid, byte[]>? layerImages)
    {
        if (!box.Visible || box.Opacity <= 0)
        {
            return false;
        }
        var frame = SKRect.Create((float)(box.X * width), (float)(box.Y * height), (float)(box.W * width), (float)(box.H * height));
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return false;
        }

        SKBitmap? picture = null;
        if (box.Kind == "image")
        {
            if (box.LogoAssetId is not { } assetId || layerImages is null || !layerImages.TryGetValue(assetId, out var bytes))
            {
                return false;
            }
            picture = ImageReferenceResolver.TryDecode(bytes);
            if (picture is null || picture.Width == 0 || picture.Height == 0)
            {
                picture?.Dispose();
                return false;
            }
        }

        using var ownedPicture = picture;
        var effect = box.Effect;
        var shadow = ShadowFor(effect, height);
        using var filter = shadow is { } s
            ? SKImageFilter.CreateDropShadow(s.Dx, s.Dy, s.Blur / 2f, s.Blur / 2f, s.Color)
            : null;
        using var layerPaint = new SKPaint { Color = SKColors.Black.WithAlpha(Alpha(box.Opacity)), ImageFilter = filter };
        using var shape = LayerShape(box, frame, height);

        var fallback = false;
        canvas.SaveLayer(layerPaint);
        canvas.Save();
        try
        {
            canvas.ClipPath(shape, SKClipOperation.Intersect, antialias: true);
            if (picture is not null)
            {
                var source = CoverSource(picture.Width, picture.Height, frame.Width, frame.Height, box.Crop ?? new OverlayImageCrop());
                canvas.DrawBitmap(picture, source, frame, new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
            else
            {
                fallback = DrawTextLayer(canvas, box, frame, height, shape, legacyFace);
            }

            var borderWidth = BorderWidthFor(effect, height);
            if (borderWidth > 0)
            {
                // Stroked on the path and clipped by it: the outer half is cut away, leaving an
                // inset border exactly borderWidth wide.
                using var stroke = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2 * borderWidth,
                    Color = ParseColor(effect!.BorderColor) ?? SKColors.White,
                    IsAntialias = true,
                };
                canvas.DrawPath(shape, stroke);
            }

            if (effect?.Preset == "bevel")
            {
                DrawBevel(canvas, box, frame, height, shape, (float)(Math.Clamp(effect.Depth, 0, 1) * 2));
            }
        }
        finally
        {
            canvas.Restore();
            canvas.Restore();
        }
        return fallback;
    }

    private static void DrawBevel(SKCanvas canvas, OverlayBox box, SKRect frame, float height, SKPath shape, float depth)
    {
        var highlightOffset = BevelHighlightOffset * height * depth;
        var shadeOffset = BevelShadeOffset * height * depth;
        using var highlightPaint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(0.45)), IsAntialias = true };
        using var shadePaint = new SKPaint { Color = SKColors.Black.WithAlpha(Alpha(0.38)), IsAntialias = true };
        FillEdge(canvas, box, frame, height, shape, highlightOffset, highlightPaint);
        FillEdge(canvas, box, frame, height, shape, -shadeOffset, shadePaint);
    }

    /// <summary>The shape minus itself shifted by (offset, offset): a sliver along the edges facing away from the shift.</summary>
    private static void FillEdge(SKCanvas canvas, OverlayBox box, SKRect frame, float height, SKPath shape, float offset, SKPaint paint)
    {
        if (offset == 0)
        {
            return;
        }
        using var shifted = LayerShape(box, SKRect.Create(frame.Left + offset, frame.Top + offset, frame.Width, frame.Height), height);
        using var edge = shape.Op(shifted, SKPathOp.Difference);
        if (edge is not null)
        {
            canvas.DrawPath(edge, paint);
        }
    }

    private static bool DrawTextLayer(SKCanvas canvas, OverlayBox box, SKRect frame, float height, SKPath shape, SKTypeface legacyFace)
    {
        if (box.Band is { Opacity: > 0 } band && ParseColor(band.Color) is { } bandColor)
        {
            using var bandPaint = new SKPaint
            {
                Color = bandColor.WithAlpha(Alpha(bandColor.Alpha / 255d * band.Opacity)),
                IsAntialias = true,
            };
            canvas.DrawPath(shape, bandPaint);
        }

        if (string.IsNullOrWhiteSpace(box.Text) || box.TextOpacity <= 0)
        {
            return false;
        }

        var resolved = OverlayFontResolver.Shared.Resolve(box.FontFamily, box.Weight);
        using var font = new SKFont(resolved?.Typeface ?? legacyFace, (float)(box.FontSize * height))
        {
            Embolden = resolved?.Embolden ?? box.Weight >= 700,
            Edging = SKFontEdging.Antialias,
        };
        var layout = LayoutText(font, box.Text, frame, box.Band?.Padding);

        var color = ParseColor(box.Color) ?? DefaultTextColor;
        using var fill = new SKPaint
        {
            Color = color.WithAlpha(Alpha(color.Alpha / 255d * Math.Clamp(box.TextOpacity, 0, 1))),
            IsAntialias = true,
        };
        var (align, x) = box.Align switch
        {
            "center" => (SKTextAlign.Center, frame.Left + (frame.Width / 2f)),
            "right" => (SKTextAlign.Right, frame.Right - layout.PadX),
            _ => (SKTextAlign.Left, frame.Left + layout.PadX),
        };
        var metrics = font.Metrics;
        var glyphBlock = metrics.Descent - metrics.Ascent;
        for (var i = 0; i < layout.Lines.Count; i++)
        {
            var lineTop = layout.Top + (i * layout.LineHeight);
            var baseline = lineTop + ((layout.LineHeight - glyphBlock) / 2f) - metrics.Ascent;
            canvas.DrawText(layout.Lines[i], x, baseline, align, font, fill);
        }
        return resolved is null;
    }

    private static byte Alpha(double ratio) => (byte)Math.Clamp(Math.Round(ratio * 255), 0, 255);
}
