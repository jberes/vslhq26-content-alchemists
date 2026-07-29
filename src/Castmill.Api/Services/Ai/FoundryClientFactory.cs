using System.ClientModel;
using Azure.AI.OpenAI;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Ai;

public sealed record FoundryCredentials(string Endpoint, string ApiKey, string Source);

public interface IFoundryClientFactory
{
    /// <summary>Resolved credentials: per-user encrypted secrets first, then app config (dev). Null if neither.</summary>
    Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct);
    /// <summary>Chat client for a model alias. All generation flows through here — one seam (G4).</summary>
    Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct);
    string? ResolveDeployment(string modelAlias);
}

public sealed class FoundryClientFactory(
    IUserSecretsService secrets,
    IOptions<AiOptions> options) : IFoundryClientFactory
{
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

    public string? ResolveDeployment(string modelAlias)
    {
        if (_options.Models.TryGetValue(modelAlias, out var deployment) && !string.IsNullOrWhiteSpace(deployment))
        {
            return deployment;
        }
        // chat-audit intentionally falls back to chat when unset.
        if (modelAlias.Equals("chat-audit", StringComparison.OrdinalIgnoreCase)
            && _options.Models.TryGetValue("chat", out var chat) && !string.IsNullOrWhiteSpace(chat))
        {
            return chat;
        }
        return null;
    }

    public async Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct)
    {
        var credentials = await ResolveCredentialsAsync(userId, ct)
            ?? throw new AiNotConfiguredException(
                "No Foundry credentials. Set Ai:Foundry:Endpoint/ApiKey in appsettings.Development.json " +
                "or store per-user secrets via /api/v1/settings/secrets.");
        var deployment = ResolveDeployment(modelAlias)
            ?? throw new AiNotConfiguredException(
                $"No deployment mapped for model alias '{modelAlias}'. Fill in Ai:Models:{modelAlias}.");

        var azureClient = new AzureOpenAIClient(new Uri(credentials.Endpoint), new ApiKeyCredential(credentials.ApiKey));
        return azureClient.GetChatClient(deployment).AsIChatClient();
    }
}

public sealed class AiNotConfiguredException(string message) : InvalidOperationException(message);
