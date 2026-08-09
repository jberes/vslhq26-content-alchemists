using Castmill.Api.Services.Images;
using OpenAI.Images;
using SkiaSharp;

namespace Castmill.Api.Services.Ai;

public interface IImageRenderer
{
    /// <summary>Generates an image for the prompt and returns it encoded as WebP.</summary>
    Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct);

    /// <summary>
    /// Slot-accurate render (B9.2): generates, then resizes + centre-crops to
    /// exactly width × height before WebP encoding. Image deployments only emit a
    /// fixed size set, so slot dimensions are always produced here.
    /// </summary>
    Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct);

    Task<byte[]> RenderExactAsync(
        Guid userId, string prompt, int width, int height, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct) =>
        RenderExactAsync(userId, prompt, width, height, modelAlias, ct);
}

public sealed class ImageRenderer(IImageProviderRegistry providers, IImageComposer composer) : IImageRenderer
{
    private const int WebpQuality = 85;

    public async Task<byte[]> RenderWebpAsync(
        Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct)
    {
        var provider = providers.Resolve(modelAlias);
        var raw = await provider.GenerateAsync(userId, prompt, aspectRatio, modelAlias, ct);
        return EncodeWebp(raw);
    }

    public async Task<byte[]> RenderExactAsync(
        Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct)
    {
        var provider = providers.Resolve(modelAlias);
        var raw = await provider.GenerateAsync(userId, prompt, AspectFor(width, height), modelAlias, ct);
        return composer.ToSlotWebp(raw, width, height);
    }

    public async Task<byte[]> RenderExactAsync(
        Guid userId, string prompt, int width, int height, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct)
    {
        var provider = providers.Resolve(modelAlias);
        var raw = await provider.GenerateAsync(
            userId, prompt, AspectFor(width, height), modelAlias, references, ct);
        return composer.ToSlotWebp(raw, width, height);
    }

    /// <summary>Closest generatable aspect for a slot — the crop pass fixes the rest.</summary>
    internal static string AspectFor(int width, int height) => ((float)width / height) switch
    {
        > 1.15f => "16:9",
        < 0.87f => "9:16",
        _ => "1:1",
    };

    /// <summary>Image deployments expose a fixed size set; map the requested aspect to the closest.</summary>
    internal static GeneratedImageSize MapSize(string aspectRatio) => aspectRatio.Trim() switch
    {
        "16:9" or "3:2" or "landscape" => new GeneratedImageSize(1536, 1024),
        "9:16" or "2:3" or "portrait" => new GeneratedImageSize(1024, 1536),
        _ => new GeneratedImageSize(1024, 1024),
    };

    /// <summary>WebP re-encode (publish format, ADR/G list): smaller than PNG at publish quality.</summary>
    internal static byte[] EncodeWebp(byte[] sourceImage)
    {
        using var bitmap = SKBitmap.Decode(sourceImage)
            ?? throw new InvalidOperationException("Model returned bytes that are not a decodable image.");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return encoded.ToArray();
    }
}
