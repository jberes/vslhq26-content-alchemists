using SkiaSharp;

namespace Castmill.Api.Services.Images;

public interface IImageComposer
{
    /// <summary>
    /// Resizes and centre-crops raw model output to exactly width × height, then
    /// WebP-encodes it (B9.2). Image deployments only emit a fixed size set, so a
    /// slot's real dimensions are always produced here, never by CSS stretching.
    /// </summary>
    byte[] ToSlotWebp(byte[] sourceImage, int width, int height);

    /// <summary>
    /// Draws a headline into the lower safe area of an already-encoded image and
    /// re-encodes it (ADR-013). Models mangle small text, so the headline is
    /// composited after generation — editing it never costs another render.
    /// </summary>
    /// <param name="backgroundColor">
    /// Optional solid band behind the text, as "#RRGGBB". Generated backgrounds are busy and
    /// unpredictable, so a drop shadow alone is not always enough to keep a headline legible;
    /// a band is the reliable answer and it is the author's call, not ours.
    /// </param>
    CompositeResult ComposeHeadline(byte[] image, string headline, bool safeArea, string? backgroundColor = null);

    /// <summary>Scales the longest edge down to <paramref name="maxEdge"/> for gallery
    /// thumbnails; the full-size WebP stays the source of truth.</summary>
    byte[] ToThumbWebp(byte[] webpImage, int maxEdge = 480);

    /// <summary>
    /// Renders an overlay spec (ADR-055) — positioned text boxes with bands — onto an encoded
    /// image, deterministically, so the published image is what the editor previewed.
    /// </summary>
    CompositeResult ComposeOverlay(byte[] image, Castmill.Core.Resources.OverlaySpec spec) =>
        throw new NotSupportedException("Overlay composition needs the real composer.");
}

/// <summary>
/// <paramref name="FontFallback"/> is true when no configured/embedded face was
/// available and the platform default was used — visible to the caller because
/// the rendered result may not match the client's preview.
/// </summary>
public sealed record CompositeResult(byte[] Image, bool FontFallback, string Typeface);

public sealed class ImageComposer(IConfiguration configuration, ILogger<ImageComposer> logger) : IImageComposer
{
    private const int WebpQuality = 85;
    /// <summary>Safe-area inset as a fraction of each edge — matches the design's dashed guide.</summary>
    internal const float SafeAreaFraction = 0.08f;
    /// <summary>Headline cap height as a fraction of output height (22 px at 720 p).</summary>
    private const float HeadlineHeightFraction = 22f / 720f;

    /// <summary>
    /// Barlow Condensed SemiBold ships with the API (OFL 1.1, see Assets/Fonts) so the
    /// compositor never depends on a system font and needs no configuration to work.
    /// Castmill:OverlayFontPath still overrides it.
    /// </summary>
    internal static readonly string DefaultFontPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "BarlowCondensed-SemiBold.ttf");

    private readonly string? _fontPath =
        configuration["Castmill:OverlayFontPath"] is { Length: > 0 } configured
            ? configured
            : DefaultFontPath;

    private SKTypeface? _typeface;
    private bool _typefaceResolved;

    public byte[] ToSlotWebp(byte[] sourceImage, int width, int height)
    {
        // TryDecode, not Decode: Skia THROWS for bytes it cannot read, so the null-coalescing
        // guard this used to have never ran and a garbled provider response surfaced as an
        // ArgumentNullException about a "codec" instead of saying what happened.
        using var source = ImageReferenceResolver.TryDecode(sourceImage)
            ?? throw new InvalidOperationException("Model returned bytes that are not a decodable image.");

        using var cropped = ContentAwareCrop(source, width, height);
        using var image = SKImage.FromBitmap(cropped);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return encoded.ToArray();
    }

    public byte[] ToThumbWebp(byte[] webpImage, int maxEdge = 480)
    {
        using var source = ImageReferenceResolver.TryDecode(webpImage)
            ?? throw new InvalidOperationException("The image to thumbnail is not decodable.");

        var scale = Math.Min(1f, (float)maxEdge / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(source.Height * scale));

        using var resized = source.Resize(
            new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Thumbnail resize failed.");
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 75)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return encoded.ToArray();
    }

    public CompositeResult ComposeHeadline(byte[] image, string headline, bool safeArea, string? backgroundColor = null)
    {
        using var bitmap = ImageReferenceResolver.TryDecode(image)
            ?? throw new InvalidOperationException("Bytes are not a decodable image.");
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);

        var (typeface, fallback) = ResolveTypeface();
        var inset = safeArea ? bitmap.Height * SafeAreaFraction : bitmap.Height * 0.03f;
        var textSize = Math.Max(12f, bitmap.Height * HeadlineHeightFraction * 3f);

        using var font = new SKFont(typeface, textSize);
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 128),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateDropShadow(0, bitmap.Height * 0.003f, 3, 3, new SKColor(0, 0, 0, 160)),
        };
        using var fill = new SKPaint { Color = new SKColor(0xF2, 0xF2, 0xF3), IsAntialias = true };

        // Shrink to fit the safe width rather than clipping — a truncated headline
        // is a silent content bug, an undersized one is merely smaller.
        var maxWidth = bitmap.Width - (inset * 2);
        while (font.MeasureText(headline) > maxWidth && font.Size > 12f)
        {
            font.Size -= 1f;
        }

        var baseline = bitmap.Height - inset;

        // The band is drawn first, sized from the measured text rather than guessed, so it
        // fits whatever the shrink-to-fit loop above settled on.
        if (ParseColor(backgroundColor) is { } band)
        {
            var metrics = font.Metrics;
            var padX = textSize * 0.4f;
            var padY = textSize * 0.25f;
            var width = font.MeasureText(headline);
            var rect = new SKRect(
                inset - padX,
                baseline + metrics.Ascent - padY,
                inset + width + padX,
                baseline + metrics.Descent + padY);

            using var bandPaint = new SKPaint { Color = band, IsAntialias = true };
            canvas.DrawRoundRect(rect, textSize * 0.12f, textSize * 0.12f, bandPaint);
        }

        canvas.DrawText(headline, inset, baseline, SKTextAlign.Left, font, shadow);
        canvas.DrawText(headline, inset, baseline, SKTextAlign.Left, font, fill);

        // The guide itself is never burned in; only the text respects it. The IsEnabled
        // guard keeps the float from being boxed when Debug logging is off (CA1873).
        if (safeArea && logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Composited headline within {Inset}px safe area", inset);
        }

        using var composed = surface.Snapshot();
        using var encoded = composed.Encode(SKEncodedImageFormat.Webp, WebpQuality)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return new CompositeResult(encoded.ToArray(), fallback, typeface.FamilyName);
    }

    public CompositeResult ComposeOverlay(byte[] image, Castmill.Core.Resources.OverlaySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var bitmap = ImageReferenceResolver.TryDecode(image)
            ?? throw new InvalidOperationException("Bytes are not a decodable image.");
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);

        var (typeface, fallback) = ResolveTypeface();
        foreach (var box in spec.Boxes)
        {
            DrawBox(canvas, box, bitmap.Width, bitmap.Height, typeface);
        }

        using var composed = surface.Snapshot();
        using var encoded = composed.Encode(SKEncodedImageFormat.Webp, WebpQuality)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return new CompositeResult(encoded.ToArray(), fallback, typeface.FamilyName);
    }

    /// <summary>
    /// One box: geometry from ratios, text wrapped to the box width and shrunk until it fits
    /// the box height, a band sized to the text (not the box) when asked for. The same maths
    /// the editor's preview runs in CSS, so the two agree to a pixel or two.
    /// </summary>
    private static void DrawBox(SKCanvas canvas, Castmill.Core.Resources.OverlayBox box, int width, int height, SKTypeface typeface)
    {
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            return;
        }
        var left = (float)(box.X * width);
        var top = (float)(box.Y * height);
        var boxWidth = MathF.Max(8f, (float)(box.W * width));
        var boxHeight = MathF.Max(8f, (float)(box.H * height));
        var textSize = MathF.Max(8f, (float)(box.FontSize * height));

        // One bundled weight (Barlow Condensed SemiBold); 700+ is emboldened synthetically.
        using var font = new SKFont(typeface, textSize) { Embolden = box.Weight >= 700, Edging = SKFontEdging.SubpixelAntialias };

        List<string> lines;
        float lineHeight;
        // Shrink to fit: wrap at the current size, and if the block is taller than the box,
        // step down. A truncated headline is a silent defect; a smaller one is merely smaller.
        while (true)
        {
            lines = Wrap(font, box.Text, boxWidth);
            lineHeight = font.Spacing;
            if (lines.Count * lineHeight <= boxHeight || font.Size <= 9f)
            {
                break;
            }
            font.Size -= 1f;
        }

        var color = ParseColor(box.Color) ?? new SKColor(0xF2, 0xF2, 0xF3);
        var textAlign = box.Align switch
        {
            "center" => SKTextAlign.Center,
            "right" => SKTextAlign.Right,
            _ => SKTextAlign.Left,
        };
        var metrics = font.Metrics;
        var blockHeight = lines.Count * lineHeight;
        var blockWidth = lines.Count == 0 ? 0 : lines.Max(line => font.MeasureText(line));
        var anchorX = textAlign switch
        {
            SKTextAlign.Center => left + (boxWidth / 2f),
            SKTextAlign.Right => left + boxWidth,
            _ => left,
        };
        var blockLeft = textAlign switch
        {
            SKTextAlign.Center => anchorX - (blockWidth / 2f),
            SKTextAlign.Right => anchorX - blockWidth,
            _ => left,
        };

        if (box.Band is { } band && ParseColor(band.Color) is { } bandColor)
        {
            var padX = (float)(textSize * band.Padding);
            var padY = (float)(textSize * band.Padding * 0.6);
            var rect = new SKRect(blockLeft - padX, top - padY, blockLeft + blockWidth + padX, top + blockHeight + padY);
            using var bandPaint = new SKPaint
            {
                Color = bandColor.WithAlpha((byte)Math.Clamp(Math.Round(band.Opacity * 255), 0, 255)),
                IsAntialias = true,
            };
            var radius = (float)(textSize * band.Radius);
            canvas.DrawRoundRect(rect, radius, radius, bandPaint);
        }
        else
        {
            // No band: a soft shadow keeps light text legible over a busy generated background.
            using var shadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 128),
                IsAntialias = true,
                ImageFilter = SKImageFilter.CreateDropShadow(0, height * 0.003f, 3, 3, new SKColor(0, 0, 0, 160)),
            };
            var shadowBaseline = top - metrics.Ascent;
            foreach (var line in lines)
            {
                canvas.DrawText(line, anchorX, shadowBaseline, textAlign, font, shadow);
                shadowBaseline += lineHeight;
            }
        }

        using var fill = new SKPaint { Color = color, IsAntialias = true };
        var baseline = top - metrics.Ascent;
        foreach (var line in lines)
        {
            canvas.DrawText(line, anchorX, baseline, textAlign, font, fill);
            baseline += lineHeight;
        }
    }

    /// <summary>Greedy word wrap; a single word wider than the box is left whole (the shrink loop handles it).</summary>
    internal static List<string> Wrap(SKFont font, string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            var current = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (current.Length > 0 && font.MeasureText(candidate) > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>
    /// "#RRGGBB" or "#RRGGBBAA" → a colour. Anything unparseable yields null and simply
    /// means no band: a bad colour must not fail a composite the user already paid to render.
    /// </summary>
    internal static SKColor? ParseColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out var color) ? color : null;

    /// <summary>
    /// Scale to cover, then crop where the CONTENT is (ADR-055). The overflow axis is scanned
    /// with a window of the target size and the window with the most detail — edge energy on
    /// a small greyscale copy — wins, so a subject the model painted off-centre survives
    /// instead of being sliced by a blind centre crop. Falls back to the centre when the
    /// frame already matches the slot (nothing to choose) or when the image is featureless.
    /// </summary>
    internal static SKBitmap ContentAwareCrop(SKBitmap source, int width, int height)
    {
        var scale = Math.Max((float)width / source.Width, (float)height / source.Height);
        var scaledWidth = (int)MathF.Ceiling(source.Width * scale);
        var scaledHeight = (int)MathF.Ceiling(source.Height * scale);
        var overflowX = scaledWidth - width;
        var overflowY = scaledHeight - height;
        if (overflowX <= 2 && overflowY <= 2)
        {
            return CentreCrop(source, width, height);
        }

        using var scaled = source.Resize(new SKImageInfo(scaledWidth, scaledHeight), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Image resize failed.");

        var (offsetX, offsetY) = CropOffset(scaled, width, height);
        var target = new SKBitmap(width, height);
        using var canvas = new SKCanvas(target);
        canvas.DrawBitmap(scaled,
            new SKRect(offsetX, offsetY, offsetX + width, offsetY + height),
            new SKRect(0, 0, width, height),
            SKSamplingOptions.Default);
        return target;
    }

    /// <summary>Where along the overflow axis the window with the most edge energy sits.</summary>
    internal static (int X, int Y) CropOffset(SKBitmap scaled, int width, int height)
    {
        var overflowX = scaled.Width - width;
        var overflowY = scaled.Height - height;
        var centre = (overflowX / 2, overflowY / 2);
        if (overflowX <= 2 && overflowY <= 2)
        {
            return centre;
        }

        // Energy on a ≤160 px greyscale copy: cheap, and detail at that scale is what a
        // viewer sees as "the subject".
        const int probe = 160;
        var factor = Math.Max(1f, Math.Max(scaled.Width, scaled.Height) / (float)probe);
        var pw = Math.Max(2, (int)(scaled.Width / factor));
        var ph = Math.Max(2, (int)(scaled.Height / factor));
        using var small = scaled.Resize(new SKImageInfo(pw, ph, SKColorType.Gray8, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear));
        if (small is null)
        {
            return centre;
        }
        var pixels = small.Bytes;
        var energyX = new double[pw];
        var energyY = new double[ph];
        for (var y = 0; y < ph - 1; y++)
        {
            for (var x = 0; x < pw - 1; x++)
            {
                var here = pixels[(y * pw) + x];
                var e = Math.Abs(here - pixels[(y * pw) + x + 1]) + Math.Abs(here - pixels[((y + 1) * pw) + x]);
                energyX[x] += e;
                energyY[y] += e;
            }
        }
        var total = energyX.Sum();
        if (total < pw * ph * 0.5)
        {
            return centre; // featureless: nothing to prefer
        }

        var x0 = overflowX > 2 ? BestWindow(energyX, (int)Math.Round(width / factor)) * factor : centre.Item1;
        var y0 = overflowY > 2 ? BestWindow(energyY, (int)Math.Round(height / factor)) * factor : centre.Item2;
        return (
            (int)Math.Clamp(Math.Round(x0), 0, overflowX),
            (int)Math.Clamp(Math.Round(y0), 0, overflowY));
    }

    private static int BestWindow(double[] energy, int window)
    {
        window = Math.Clamp(window, 1, energy.Length);
        var best = 0;
        var bestSum = double.MinValue;
        var sum = 0d;
        for (var i = 0; i < energy.Length; i++)
        {
            sum += energy[i];
            if (i >= window)
            {
                sum -= energy[i - window];
            }
            if (i >= window - 1 && sum > bestSum)
            {
                bestSum = sum;
                best = i - window + 1;
            }
        }
        return best;
    }

    /// <summary>Scale to cover, then crop the centre — aspect is preserved, never squashed.</summary>
    internal static SKBitmap CentreCrop(SKBitmap source, int width, int height)
    {
        var scale = Math.Max((float)width / source.Width, (float)height / source.Height);
        var scaledWidth = (int)MathF.Ceiling(source.Width * scale);
        var scaledHeight = (int)MathF.Ceiling(source.Height * scale);

        using var scaled = source.Resize(new SKImageInfo(scaledWidth, scaledHeight), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Image resize failed.");

        var target = new SKBitmap(width, height);
        using var canvas = new SKCanvas(target);
        canvas.DrawBitmap(scaled, new SKRect(
            (scaledWidth - width) / 2f,
            (scaledHeight - height) / 2f,
            ((scaledWidth - width) / 2f) + width,
            ((scaledHeight - height) / 2f) + height),
            new SKRect(0, 0, width, height),
            SKSamplingOptions.Default);
        return target;
    }

    /// <summary>
    /// Overlay text must not depend on a system font: Linux App Service images
    /// ship few or none. A licence-clean face is configured via
    /// Castmill:OverlayFontPath; the platform default is a visible fallback.
    /// </summary>
    private (SKTypeface Typeface, bool Fallback) ResolveTypeface()
    {
        if (!_typefaceResolved)
        {
            _typefaceResolved = true;
            if (!string.IsNullOrWhiteSpace(_fontPath) && File.Exists(_fontPath))
            {
                _typeface = SKTypeface.FromFile(_fontPath);
                if (_typeface is null)
                {
                    logger.LogWarning("Castmill:OverlayFontPath is set but the file could not be loaded as a typeface");
                }
            }
            else if (!string.IsNullOrWhiteSpace(_fontPath))
            {
                logger.LogWarning("Castmill:OverlayFontPath points at a missing file; using the platform default face");
            }
        }
        return _typeface is not null
            ? (_typeface, false)
            : (SKTypeface.Default, true);
    }
}
