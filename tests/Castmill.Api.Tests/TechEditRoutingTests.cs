using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

/// <summary>
/// Second-pass routing (ADR-020). The Tech Edit is only worth running against a DIFFERENT
/// model family, so the alias has to reach the configured text provider when it is ready —
/// and quietly fall back to Foundry when it is not, because an unset key should degrade the
/// pass, never fail the user's click.
/// </summary>
public sealed class TechEditRoutingTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public async Task The_alias_resolves_to_the_text_provider_when_it_has_a_key()
    {
        var registry = Registry(enabled: true, key: "sk-ant-test");

        Assert.Equal("anthropic",
            await registry.ResolveNameAsync(User, FoundryClientFactory.TechEditAlias, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_stored_key_it_falls_back_to_foundry_rather_than_failing()
    {
        var registry = Registry(enabled: true, key: null);

        Assert.Equal("foundry",
            await registry.ResolveNameAsync(User, FoundryClientFactory.TechEditAlias, TestContext.Current.CancellationToken));

        var status = await registry.StatusAsync(User, TestContext.Current.CancellationToken);
        var provider = Assert.Single(status);
        Assert.False(provider.Ready);
        Assert.Contains("TechEditKey", provider.Reason, StringComparison.Ordinal);
    }

    /// <summary>A provider that is not explicitly enabled is never resolvable, and never listed.</summary>
    [Fact]
    public async Task A_disabled_provider_is_invisible()
    {
        var registry = Registry(enabled: false, key: "sk-ant-test");

        Assert.Equal("foundry",
            await registry.ResolveNameAsync(User, FoundryClientFactory.TechEditAlias, TestContext.Current.CancellationToken));
        Assert.Empty(await registry.StatusAsync(User, TestContext.Current.CancellationToken));
    }

    /// <summary>Pass-1 generation must not wander onto the second-pass provider.</summary>
    [Fact]
    public async Task The_ordinary_chat_alias_still_goes_to_foundry()
    {
        var registry = Registry(enabled: true, key: "sk-ant-test");

        Assert.Equal("foundry", await registry.ResolveNameAsync(User, "chat", TestContext.Current.CancellationToken));
    }

    /// <summary>An unmapped tech-edit alias behaves like chat-audit: same-family second pass.</summary>
    [Fact]
    public async Task An_unmapped_tech_edit_alias_falls_back_to_the_chat_deployment()
    {
        var factory = new FoundryClientFactory(new NoSecrets(), Options.Create(new AiOptions
        {
            Foundry = new AiOptions.FoundryOptions { Endpoint = "https://main.openai.azure.com", ApiKey = "k" },
            Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["chat"] = "gpt-5.6-terra" },
        }));

        var target = await factory.ResolveTargetAsync(
            User, FoundryClientFactory.TechEditAlias, TestContext.Current.CancellationToken);

        Assert.Equal("gpt-5.6-terra", target!.Deployment);
    }

    // ---- helpers ---------------------------------------------------------------

    private static ChatProviderRegistry Registry(bool enabled, string? key)
    {
        var options = Options.Create(new AiOptions
        {
            Foundry = new AiOptions.FoundryOptions { Endpoint = "https://main.openai.azure.com", ApiKey = "k" },
            Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = "gpt-5.6-terra",
                [FoundryClientFactory.TechEditAlias] = "anthropic",
            },
        });

        var provider = new AnthropicChatProvider(
            "anthropic",
            new AiOptions.TextProviderOptions { Enabled = enabled, Kind = "anthropic", Model = "claude-opus-5" },
            new StubSecrets(key));

        return new ChatProviderRegistry(
            new FoundryClientFactory(new StubSecrets(key), options), options, [provider]);
    }

    private sealed class StubSecrets(string? techEditKey) : IUserSecretsService
    {
        public Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct) =>
            Task.FromResult(kind == SecretKind.TechEditKey ? techEditKey : null);

        public Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<SecretKind, DateTimeOffset>>(new Dictionary<SecretKind, DateTimeOffset>());
    }

    private sealed class NoSecrets : IUserSecretsService
    {
        public Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<SecretKind, DateTimeOffset>>(new Dictionary<SecretKind, DateTimeOffset>());
    }
}

/// <summary>
/// The model that would actually be reached. Pinned as a test because "use the latest best
/// model" is a decision that silently rots: Opus 4.8 is a previous-generation model and there
/// is no "Opus 5.8" at all.
/// </summary>
public sealed class TechEditModelDefaultTests
{
    [Fact]
    public void The_default_model_is_the_current_flagship_opus()
    {
        Assert.Equal("claude-opus-5", AnthropicChatProvider.DefaultModel);
    }
}
