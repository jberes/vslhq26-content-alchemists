using System.ClientModel;
using Azure.AI.OpenAI;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Ai;

public sealed record FoundryCredentials(string Endpoint, string ApiKey, string Source);

/// <summary>Fully resolved model target: which resource to call and which deployment on it.</summary>
public sealed record FoundryTarget(FoundryCredentials Credentials, string Deployment);

public interface IFoundryClientFactory
{
    /// <summary>Default-resource credentials: per-user encrypted secrets first, then app config (dev). Null if neither.</summary>
    Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct);
    /// <summary>Deployment name for an alias (resource prefix stripped); null when unmapped.</summary>
    string? ResolveDeployment(string modelAlias);
    /// <summary>Alias → endpoint+key+deployment, honoring "resource:deployment" routing.</summary>
    Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct);
    /// <summary>Chat client for a model alias. All generation flows through here — one seam (G4).</summary>
    Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct);
}

public sealed class FoundryClientFactory(
    IUserSecretsService secrets,
    IOptions<AiOptions> options) : IFoundryClientFactory
{
    /// <summary>Model alias for the second-pass Tech Edit (ADR-020).</summary>
    public const string TechEditAlias = "chat-tech-edit";

    private readonly AiOptions _options = options.Value;

    public async Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct)
    {
        // BYO per-user credentials (ADR-004) take precedence; the config-file
        // fallback is the single-user dev convenience.
        var endpoint = await secrets.GetAsync(userId, SecretKind.FoundryEndpoint, ct);
        var key = await secrets.GetAsync(userId, SecretKind.FoundryKey, ct);
        if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key))
        {
            return new FoundryCredentials(endpoint, key, "user-secret");
        }

        if (!string.IsNullOrWhiteSpace(_options.Foundry.Endpoint) && !string.IsNullOrWhiteSpace(_options.Foundry.ApiKey))
        {
            return new FoundryCredentials(_options.Foundry.Endpoint, _options.Foundry.ApiKey, "config");
        }
        return null;
    }

    public string? ResolveDeployment(string modelAlias) =>
        ResolveMapping(modelAlias)?.Deployment;

    public async Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct)
    {
        var mapping = ResolveMapping(modelAlias);
        if (mapping is null)
        {
            return null;
        }

        if (mapping.ResourceName is not null)
        {
            // Named-resource routing: the alias pins both endpoint and key.
            if (!_options.Resources.TryGetValue(mapping.ResourceName, out var resource)
                || string.IsNullOrWhiteSpace(resource.Endpoint) || string.IsNullOrWhiteSpace(resource.ApiKey))
            {
                throw new AiNotConfiguredException(
                    $"Alias '{modelAlias}' routes to resource '{mapping.ResourceName}' but " +
                    $"Ai:Resources:{mapping.ResourceName} has no Endpoint/ApiKey configured.");
            }
            return new FoundryTarget(
                new FoundryCredentials(resource.Endpoint, resource.ApiKey, $"config:{mapping.ResourceName}"),
                mapping.Deployment);
        }

        var credentials = await ResolveCredentialsAsync(userId, ct);
        return credentials is null ? null : new FoundryTarget(credentials, mapping.Deployment);
    }

    public async Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct)
    {
        var target = await ResolveTargetAsync(userId, modelAlias, ct)
            ?? throw new AiNotConfiguredException(
                $"No Foundry credentials or deployment for alias '{modelAlias}'. " +
                "Fill in Ai:Foundry + Ai:Models in appsettings.Development.json " +
                "or store per-user secrets via /api/v1/settings/secrets.");

        var azureClient = new AzureOpenAIClient(
            new Uri(target.Credentials.Endpoint), new ApiKeyCredential(target.Credentials.ApiKey));
        return azureClient.GetChatClient(target.Deployment).AsIChatClient();
    }

    private sealed record AliasMapping(string Deployment, string? ResourceName);

    private AliasMapping? ResolveMapping(string modelAlias)
    {
        if (!_options.Models.TryGetValue(modelAlias, out var value) || string.IsNullOrWhiteSpace(value))
        {
            // The second-opinion aliases intentionally fall back to chat when unset, so a
            // deployment that has not configured them still gets a (same-family) pass rather
            // than a failure.
            if (modelAlias.Equals("chat-audit", StringComparison.OrdinalIgnoreCase)
                || modelAlias.Equals(TechEditAlias, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMapping("chat");
            }
            return null;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        return separator > 0
            ? new AliasMapping(value[(separator + 1)..], value[..separator])
            : new AliasMapping(value, null);
    }
}

public sealed class AiNotConfiguredException(string message) : InvalidOperationException(message);

/// <summary>The provider's safety system refused the render. The message is written for the
/// producer — it travels into the run's failure list verbatim, unlike other exceptions,
/// which surface only as a type name.</summary>
public sealed class ImageModerationException(string message) : InvalidOperationException(message);
