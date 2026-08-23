using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

/// <summary>Model-alias routing: default resource vs "resource:deployment" pinning.</summary>
public sealed class FoundryRoutingTests
{
    private sealed class NoSecrets : IUserSecretsService
    {
        public Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<SecretKind, DateTimeOffset>>(new Dictionary<SecretKind, DateTimeOffset>());
    }

    private static FoundryClientFactory CreateFactory() => new(new NoSecrets(), Options.Create(new AiOptions
    {
        Foundry = new AiOptions.FoundryOptions { Endpoint = "https://main.openai.azure.com", ApiKey = "main-key" },
        Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chat"] = "gpt-5.6-terra",
            ["image"] = "eastus2:gpt-image-2",
            ["broken"] = "nowhere:model",
        },
        Resources = new Dictionary<string, AiOptions.FoundryOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["eastus2"] = new() { Endpoint = "https://east.cognitiveservices.azure.com", ApiKey = "east-key" },
        },
    }));

    [Fact]
    public async Task Plain_alias_uses_the_default_resource()
    {
        var target = await CreateFactory().ResolveTargetAsync(Guid.NewGuid(), "chat", TestContext.Current.CancellationToken);
        Assert.NotNull(target);
        Assert.Equal("gpt-5.6-terra", target.Deployment);
        Assert.Equal("https://main.openai.azure.com", target.Credentials.Endpoint);
    }

    [Fact]
    public async Task Prefixed_alias_routes_to_the_named_resource()
    {
        var target = await CreateFactory().ResolveTargetAsync(Guid.NewGuid(), "image", TestContext.Current.CancellationToken);
        Assert.NotNull(target);
        Assert.Equal("gpt-image-2", target.Deployment);
        Assert.Equal("https://east.cognitiveservices.azure.com", target.Credentials.Endpoint);
        Assert.Equal("east-key", target.Credentials.ApiKey);
    }

    [Fact]
    public async Task Unknown_resource_prefix_fails_with_actionable_config_error()
    {
        var ex = await Assert.ThrowsAsync<AiNotConfiguredException>(() =>
            CreateFactory().ResolveTargetAsync(Guid.NewGuid(), "broken", TestContext.Current.CancellationToken));
        Assert.Contains("Ai:Resources:nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_audit_falls_back_to_chat_when_unmapped()
    {
        var target = await CreateFactory().ResolveTargetAsync(Guid.NewGuid(), "chat-audit", TestContext.Current.CancellationToken);
        Assert.Equal("gpt-5.6-terra", target!.Deployment);
    }

    [Fact]
    public void App_service_safe_model_keys_restore_hyphenated_aliases()
    {
        var options = new AiOptions
        {
            Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat_audit"] = "gpt-audit",
                ["image_alt"] = "mai-image",
            },
        };

        options.NormalizeAppServiceKeys();

        Assert.Equal("gpt-audit", options.Models["chat-audit"]);
        Assert.Equal("mai-image", options.Models["image-alt"]);
    }
}
