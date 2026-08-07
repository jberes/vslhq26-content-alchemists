using Anthropic;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Ai;

public sealed record ChatProviderStatus(string Name, bool Ready, string? Reason);

/// <summary>
/// Text-generation seam (ADR-020). Foundry is the default and serves every pass-1 generator
/// and the whole fan-out; additional providers are config-gated behind
/// <c>Ai:TextProviders</c> and carry their own per-user credential. This exists so the
/// second-pass Tech Edit can deliberately cross model families — a second opinion from the
/// same family is worth much less than one from a different one.
/// </summary>
public interface IChatProvider
{
    string Name { get; }
    bool IsEnabled { get; }
    Task<ChatProviderStatus> StatusAsync(Guid userId, CancellationToken ct);
    Task<IChatClient> CreateChatClientAsync(Guid userId, CancellationToken ct);
}

public interface IChatProviderRegistry
{
    /// <summary>
    /// Alias → chat client. An alias whose <c>Ai:Models</c> value names a ready
    /// <c>Ai:TextProviders</c> entry resolves to that provider; anything else is a Foundry
    /// model alias. A configured-but-not-ready provider falls back to Foundry rather than
    /// failing, so an unset key degrades the Tech Edit to a same-family pass.
    /// </summary>
    Task<IChatClient> ResolveAsync(Guid userId, string modelAlias, CancellationToken ct);

    /// <summary>Which provider actually serves this alias right now — for narration and the prompt log.</summary>
    Task<string> ResolveNameAsync(Guid userId, string modelAlias, CancellationToken ct);

    Task<IReadOnlyList<ChatProviderStatus>> StatusAsync(Guid userId, CancellationToken ct);
}

public sealed class ChatProviderRegistry(
    IFoundryClientFactory foundry,
    IOptions<AiOptions> options,
    IEnumerable<IChatProvider> providers) : IChatProviderRegistry
{
    private readonly AiOptions _options = options.Value;

    private readonly Dictionary<string, IChatProvider> _byName =
        providers.Where(p => p.IsEnabled).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<IChatClient> ResolveAsync(Guid userId, string modelAlias, CancellationToken ct)
    {
        if (await ResolveProviderAsync(userId, modelAlias, ct) is { } provider)
        {
            return await provider.CreateChatClientAsync(userId, ct);
        }
        return await foundry.CreateChatClientAsync(userId, modelAlias, ct);
    }

    public async Task<string> ResolveNameAsync(Guid userId, string modelAlias, CancellationToken ct) =>
        await ResolveProviderAsync(userId, modelAlias, ct) is { } provider ? provider.Name : "foundry";

    public async Task<IReadOnlyList<ChatProviderStatus>> StatusAsync(Guid userId, CancellationToken ct)
    {
        var statuses = new List<ChatProviderStatus>();
        foreach (var provider in _byName.Values)
        {
            statuses.Add(await provider.StatusAsync(userId, ct));
        }
        return statuses;
    }

    /// <summary>
    /// Null means "Foundry serves this alias". A provider that is configured but not ready
    /// (no key stored) also returns null: falling back beats failing the user's click.
    /// </summary>
    private async Task<IChatProvider?> ResolveProviderAsync(Guid userId, string modelAlias, CancellationToken ct)
    {
        if (!_options.Models.TryGetValue(modelAlias, out var value)
            || string.IsNullOrWhiteSpace(value)
            || !_byName.TryGetValue(value, out var provider))
        {
            return null;
        }

        var status = await provider.StatusAsync(userId, ct);
        return status.Ready ? provider : null;
    }
}

/// <summary>
/// The Anthropic adapter. <c>AsIChatClient</c> hands back a
/// <see cref="Microsoft.Extensions.AI.IChatClient"/>, so ADR-005 holds and no call site
/// learns a second abstraction.
/// </summary>
public sealed class AnthropicChatProvider(
    string name,
    AiOptions.TextProviderOptions options,
    IUserSecretsService secrets) : IChatProvider
{
    /// <summary>
    /// Current default. Claude Opus 5 is the newest and most capable Opus; there is no
    /// "Opus 5.8" — 4.8 is the previous generation and is now a legacy model. Overridable
    /// per deployment through <c>Ai:TextProviders:{name}:Model</c>.
    /// </summary>
    public const string DefaultModel = "claude-opus-5";

    /// <summary>
    /// Generous on purpose. From Opus 5 onward, adaptive thinking is ON for any request that
    /// omits a <c>thinking</c> field, and <c>max_tokens</c> is a hard limit on thinking PLUS
    /// response text — so a budget sized only for the visible answer truncates the rewrite.
    /// A Tech Edit re-emits a whole 1,500–2,500-word artifact, and the tokenizer introduced
    /// with Opus 4.7 counts roughly 1–1.35× what older ones did.
    /// </summary>
    private const int MaxOutputTokens = 32_000;

    public string Name => name;

    public bool IsEnabled => options.Enabled;

    public async Task<ChatProviderStatus> StatusAsync(Guid userId, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return new ChatProviderStatus(Name, false, $"Not enabled in Ai:TextProviders:{Name}.");
        }

        if (!options.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatProviderStatus(Name, false, $"Unknown provider kind '{options.Kind}'.");
        }

        var key = await secrets.GetAsync(userId, SecretKind.TechEditKey, ct);
        return string.IsNullOrWhiteSpace(key)
            ? new ChatProviderStatus(Name, false,
                "No API key stored. Set it via PUT /api/v1/settings/secrets/TechEditKey.")
            : new ChatProviderStatus(Name, true, null);
    }

    public async Task<IChatClient> CreateChatClientAsync(Guid userId, CancellationToken ct)
    {
        var key = await secrets.GetAsync(userId, SecretKind.TechEditKey, ct);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiNotConfiguredException(
                $"Text provider '{Name}' has no API key. " +
                "Store one via PUT /api/v1/settings/secrets/TechEditKey.");
        }

        // The SDK owns its own transport, so this deliberately does NOT go through
        // IHttpClientFactory: AddStandardResilienceHandler's ~30s attempt timeout would
        // abort a long rewrite, and its retries would re-bill a paid generation.
        var client = new AnthropicClient
        {
            ApiKey = key,
            Timeout = TimeSpan.FromMinutes(5),
            MaxRetries = 1,
        };

        // No temperature / top_p / top_k anywhere on this path: from Opus 5 onward a
        // non-default value for any of them is a 400, not a nudge. Thinking is likewise
        // left unset so the model runs adaptive, which is the only supported mode.
        var model = string.IsNullOrWhiteSpace(options.Model) ? DefaultModel : options.Model;
        return client.AsIChatClient(model, MaxOutputTokens);
    }
}

/// <summary>Registers every configured text provider. Foundry needs no entry — it is the fallback.</summary>
public static class ChatProviderRegistration
{
    public static IServiceCollection AddTextProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var providers = configuration
            .GetSection($"{AiOptions.SectionName}:TextProviders")
            .Get<Dictionary<string, AiOptions.TextProviderOptions>>() ?? [];

        foreach (var (name, options) in providers)
        {
            services.AddScoped<IChatProvider>(sp => new AnthropicChatProvider(
                name, options, sp.GetRequiredService<IUserSecretsService>()));
        }

        services.AddScoped<IChatProviderRegistry, ChatProviderRegistry>();
        return services;
    }
}
