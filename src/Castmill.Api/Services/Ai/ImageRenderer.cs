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

    /// <summary>Region edit of an existing take (ADR-055): the provider repaints the masked area,
    /// the result is fitted back to the take's own size and WebP-encoded. No house rules are
    /// appended: the frame IS the take, nothing is cropped.</summary>
    Task<byte[]> RenderEditAsync(
        Guid userId, string instruction, byte[] image, byte[] maskPng, int width, int height, string? modelAlias, CancellationToken ct) =>
        throw new NotSupportedException("Region edits need the real renderer.");

    /// <summary>The frame the resolved provider paints for this slot (ADR-055) — for the
    /// prompt preview's crop figures and the studio's per-model crop badge.</summary>
    Task<(int Width, int Height)> FrameForAsync(Guid userId, int width, int height, string? modelAlias, CancellationToken ct) =>
        Task.FromResult(ImageRenderer.FrameFor(width, height));
}

public sealed class ImageRenderer(IImageProviderRegistry providers, IImageComposer composer) : IImageRenderer
{
    private const int WebpQuality = 85;

    // Every render path applies ImagePromptRules here, at the one choke point every
    // generation passes through: the crop below is unconditional, so the safe-margin
    // rule must be too (a call site or a user prompt cannot opt out of it).
    public async Task<byte[]> RenderWebpAsync(
        Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct)
    {
        var provider = providers.Resolve(modelAlias);
        var raw = await provider.GenerateAsync(
            userId, ImagePromptRules.Apply(prompt), aspectRatio, modelAlias, ct);
        return EncodeWebp(raw);
    }

    // The provider is asked for the slot's EXACT size (ADR-055): MAI paints it, Gemini picks
    // its nearest native ratio, gpt-image maps to one of three fixed frames. The safe-margin
    // rules are then written against the frame that provider will really return, so the
    // numbers in the prompt match the crop that follows.
    public async Task<byte[]> RenderExactAsync(
        Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct)
    {
        var provider = providers.Resolve(modelAlias);
        var frame = await provider.FrameForAsync(userId, width, height, modelAlias, ct);
        var raw = await provider.GenerateAsync(
            userId, ImagePromptRules.Apply(prompt, width, height, frame.Width, frame.Height),
            ImageAspect.Describe(width, height), modelAlias, ct);
        return composer.ToSlotWebp(raw, width, height);
    }

    public async Task<byte[]> RenderExactAsync(
        Guid userId, string prompt, int width, int height, string? modelAlias,
        IReadOnlyList<ImageReference> references, CancellationToken ct)
    {
        var provider = providers.Resolve(modelAlias);
        var frame = await provider.FrameForAsync(userId, width, height, modelAlias, ct);
        var raw = await provider.GenerateAsync(
            userId, ImagePromptRules.Apply(prompt, width, height, frame.Width, frame.Height),
            ImageAspect.Describe(width, height), modelAlias, references, ct);
        return composer.ToSlotWebp(raw, width, height);
    }

    public Task<(int Width, int Height)> FrameForAsync(
        Guid userId, int width, int height, string? modelAlias, CancellationToken ct) =>
        providers.Resolve(modelAlias).FrameForAsync(userId, width, height, modelAlias, ct);

    public async Task<byte[]> RenderEditAsync(
        Guid userId, string instruction, byte[] image, byte[] maskPng, int width, int height, string? modelAlias, CancellationToken ct)
    {
        var raw = await providers.Resolve(modelAlias).EditAsync(userId, instruction, image, maskPng, modelAlias, ct);
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

    /// <summary>The frame a provider paints for a slot of this size, before the centre-crop.</summary>
    public static (int Width, int Height) FrameFor(int width, int height) =>
        FrameDimensions(AspectFor(width, height));

    private static (int Width, int Height) FrameDimensions(string aspectRatio) =>
        aspectRatio.Trim() switch
        {
            "16:9" or "3:2" or "landscape" => (1536, 1024),
            "9:16" or "2:3" or "portrait" => (1024, 1536),
            _ => (1024, 1024),
        };

    /// <summary>WebP re-encode (publish format, ADR/G list): smaller than PNG at publish quality.</summary>
    internal static byte[] EncodeWebp(byte[] sourceImage)
    {
        using var bitmap = Castmill.Api.Services.Images.ImageReferenceResolver.TryDecode(sourceImage)
            ?? throw new InvalidOperationException("Model returned bytes that are not a decodable image.");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return encoded.ToArray();
    }
}
