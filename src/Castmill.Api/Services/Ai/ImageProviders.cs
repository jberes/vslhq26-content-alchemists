using System.ClientModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.AI.OpenAI;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Options;
using OpenAI.Images;

namespace Castmill.Api.Services.Ai;

public sealed record ImageProviderStatus(string Name, bool Ready, string? Reason);

/// <summary>
/// Image-generation seam (ADR-015). Foundry is the default and the only provider
/// enabled out of the box; additional providers are config-gated and carry their
/// own per-user credential. Text generation has no such seam — it is Foundry-only.
/// </summary>
public interface IImageProvider
{
    string Name { get; }
    Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct);
    /// <summary>Returns raw encoded image bytes (PNG/JPEG/WebP) — the caller crops and re-encodes.</summary>
    Task<byte[]> GenerateAsync(Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct);
}

public interface IImageProviderRegistry
{
    /// <summary>
    /// Routes on the alias: a value naming a configured provider selects it,
    /// anything else is a Foundry model alias.
    /// </summary>
    IImageProvider Resolve(string? modelAliasOrProvider);
    Task<IReadOnlyList<ImageProviderStatus>> StatusAsync(Guid userId, CancellationToken ct);
}

public sealed class ImageProviderRegistry(
    FoundryImageProvider foundry,
    IEnumerable<ExternalImageProvider> external) : IImageProviderRegistry
{
    private readonly Dictionary<string, IImageProvider> _byName =
        external.Where(p => p.IsEnabled).ToDictionary<ExternalImageProvider, string, IImageProvider>(
            p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

    public IImageProvider Resolve(string? modelAliasOrProvider) =>
        modelAliasOrProvider is not null && _byName.TryGetValue(modelAliasOrProvider, out var provider)
            ? provider
            : foundry;

    public async Task<IReadOnlyList<ImageProviderStatus>> StatusAsync(Guid userId, CancellationToken ct)
    {
        var statuses = new List<ImageProviderStatus> { await foundry.StatusAsync(userId, ct) };
        foreach (var provider in _byName.Values)
        {
            statuses.Add(await provider.StatusAsync(userId, ct));
        }
        return statuses;
    }
}

/// <summary>Default provider: the Foundry image deployments behind the alias table.</summary>
public sealed class FoundryImageProvider(IFoundryClientFactory clients) : IImageProvider
{
    public string Name => "foundry";

    public async Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var target = await clients.ResolveTargetAsync(userId, "image", ct);
            return target is null
                ? new ImageProviderStatus(Name, false, "No credentials or no deployment mapped for the 'image' alias.")
                : new ImageProviderStatus(Name, true, null);
        }
        catch (AiNotConfiguredException ex)
        {
            return new ImageProviderStatus(Name, false, ex.Message);
        }
    }

    public async Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct)
    {
        // A slot may carry a model alias OR a provider name (the studio's model radio
        // writes the latter — Resolve() routes on it). To this provider its own name
        // means "your default deployment", never an alias-table lookup.
        var alias = string.IsNullOrWhiteSpace(modelAlias) || modelAlias.Equals(Name, StringComparison.OrdinalIgnoreCase)
            ? "image"
            : modelAlias;
        var target = await clients.ResolveTargetAsync(userId, alias, ct)
            ?? throw new AiNotConfiguredException($"No Foundry credentials/deployment for image alias '{alias}'.");

        var azureClient = new AzureOpenAIClient(
            new Uri(target.Credentials.Endpoint), new ApiKeyCredential(target.Credentials.ApiKey));
        var imageClient = azureClient.GetImageClient(target.Deployment);

        // gpt-image-* models reject the response_format parameter (they always
        // return b64) — only Size may be set.
        var generated = await imageClient.GenerateImageAsync(prompt, new ImageGenerationOptions
        {
            Size = ImageRenderer.MapSize(aspectRatio),
        }, ct);
        return generated.Value.ImageBytes.ToArray();
    }
}

/// <summary>
/// Optional non-Foundry provider (ADR-015), off unless <c>Ai:Providers:{name}:Enabled</c>
/// is true. Speaks the widely-used OpenAI-compatible images shape
/// (<c>POST {Endpoint}/images/generations</c> → <c>data[0].b64_json</c>); a vendor
/// with a different shape needs its own adapter, not a config tweak.
/// </summary>
public sealed class ExternalImageProvider(
    string name,
    AiOptions.ImageProviderOptions options,
    IHttpClientFactory httpClients,
    IUserSecretsService secrets) : IImageProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => name;
    public bool IsEnabled => options.Enabled && !string.IsNullOrWhiteSpace(options.Endpoint);

    public async Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return new ImageProviderStatus(Name, false, $"Ai:Providers:{Name} is disabled or has no Endpoint.");
        }
        var key = await secrets.GetAsync(userId, SecretKind.ImageProviderKey, ct);
        return string.IsNullOrWhiteSpace(key)
            ? new ImageProviderStatus(Name, false,
                "No provider key stored. Set it via PUT /api/v1/settings/secrets/ImageProviderKey.")
            : new ImageProviderStatus(Name, true, null);
    }

    public async Task<byte[]> GenerateAsync(
        Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            throw new AiNotConfiguredException($"Image provider '{Name}' is not enabled.");
        }
        var key = await secrets.GetAsync(userId, SecretKind.ImageProviderKey, ct)
            ?? throw new AiNotConfiguredException(
                $"No key stored for image provider '{Name}'. Set ImageProviderKey in /api/v1/settings/secrets.");

        var client = httpClients.CreateClient("imageprovider");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(options.Endpoint.TrimEnd('/') + "/"), "images/generations"))
        {
            Content = JsonContent.Create(new
            {
                model = string.IsNullOrWhiteSpace(modelAlias) || modelAlias.Equals(Name, StringComparison.OrdinalIgnoreCase)
                    ? options.Model
                    : modelAlias,
                prompt,
                n = 1,
                size = SizeFor(aspectRatio),
            }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Never echo the body: provider errors can quote the request, and the
            // request carries the credential header context.
            throw new InvalidOperationException(
                $"Image provider '{Name}' returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var b64 = doc.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString()
            ?? throw new InvalidOperationException($"Image provider '{Name}' returned no image data.");
        return Convert.FromBase64String(b64);
    }

    private static string SizeFor(string aspectRatio) => aspectRatio.Trim() switch
    {
        "16:9" or "3:2" or "landscape" => "1536x1024",
        "9:16" or "2:3" or "portrait" => "1024x1536",
        _ => "1024x1024",
    };
}

/// <summary>Registers the Foundry provider plus every configured external one.</summary>
public static class ImageProviderRegistration
{
    public static IServiceCollection AddImageProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<FoundryImageProvider>();

        var providers = configuration
            .GetSection($"{AiOptions.SectionName}:Providers")
            .Get<Dictionary<string, AiOptions.ImageProviderOptions>>() ?? [];

        foreach (var (name, options) in providers)
        {
            services.AddScoped(sp => new ExternalImageProvider(
                name, options,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IUserSecretsService>()));
        }

        services.AddScoped<IImageProviderRegistry, ImageProviderRegistry>();
        return services;
    }
}
