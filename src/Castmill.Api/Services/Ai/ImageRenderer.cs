using System.ClientModel;
using Azure.AI.OpenAI;
using OpenAI.Images;
using SkiaSharp;

namespace Castmill.Api.Services.Ai;

public interface IImageRenderer
{
    /// <summary>Generates an image for the prompt and returns it encoded as WebP.</summary>
    Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct);
}

public sealed class ImageRenderer(IFoundryClientFactory clients) : IImageRenderer
{
    private const int WebpQuality = 85;

    public async Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct)
    {
        var credentials = await clients.ResolveCredentialsAsync(userId, ct)
            ?? throw new AiNotConfiguredException("No Foundry credentials configured for image generation.");
        var deployment = clients.ResolveDeployment(modelAlias)
            ?? throw new AiNotConfiguredException($"No deployment mapped for image alias '{modelAlias}'.");

        var azureClient = new AzureOpenAIClient(new Uri(credentials.Endpoint), new ApiKeyCredential(credentials.ApiKey));
        var imageClient = azureClient.GetImageClient(deployment);

        var generated = await imageClient.GenerateImageAsync(prompt, new ImageGenerationOptions
        {
            Size = MapSize(aspectRatio),
            ResponseFormat = GeneratedImageFormat.Bytes,
        }, ct);

        return EncodeWebp(generated.Value.ImageBytes.ToArray());
    }

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
