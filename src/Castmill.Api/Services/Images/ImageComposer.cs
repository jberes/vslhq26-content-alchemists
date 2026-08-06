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
    CompositeResult ComposeHeadline(byte[] image, string headline, bool safeArea);

    /// <summary>Scales the longest edge down to <paramref name="maxEdge"/> for gallery
    /// thumbnails; the full-size WebP stays the source of truth.</summary>
    byte[] ToThumbWebp(byte[] webpImage, int maxEdge = 480);
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
        using var source = SKBitmap.Decode(sourceImage)
            ?? throw new InvalidOperationException("Model returned bytes that are not a decodable image.");

        using var cropped = CentreCrop(source, width, height);
        using var image = SKImage.FromBitmap(cropped);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return encoded.ToArray();
    }

    public byte[] ToThumbWebp(byte[] webpImage, int maxEdge = 480)
    {
        using var source = SKBitmap.Decode(webpImage)
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

    public CompositeResult ComposeHeadline(byte[] image, string headline, bool safeArea)
    {
        using var bitmap = SKBitmap.Decode(image)
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
