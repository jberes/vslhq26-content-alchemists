using Castmill.Api.Services.Ai;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

/// <summary>
/// A model alias is one alias however it is spelled (ADR-072).
///
/// The config exporter rewrites "-" to "_" because an App Service setting becomes a Linux
/// environment variable, so production held Ai:Models:image_alt while the app asked for
/// image-alt. Nothing matched: the studio offered "image, image-alt, image_alt" as three
/// models to compare, choosing the hyphenated one returned 500, and chat-audit fell through
/// to chat so every second-opinion pass silently ran on the drafting model.
/// </summary>
public sealed class ModelAliasSpellingTests
{
    [Theory]
    [InlineData("image-alt")]
    [InlineData("image_alt")]
    public void Either_spelling_resolves_to_the_deployment(string requested)
    {
        // Production's shape: the exporter wrote the underscore form.
        var factory = Factory(new Dictionary<string, string> { ["image_alt"] = "MAI-Image-2.5-Pro" });
        Assert.Equal("MAI-Image-2.5-Pro", factory.ResolveDeployment(requested));
    }

    [Theory]
    [InlineData("chat-audit")]
    [InlineData("chat_audit")]
    public void The_second_opinion_alias_resolves_rather_than_falling_back_to_chat(string requested)
    {
        var factory = Factory(new Dictionary<string, string>
        {
            ["chat"] = "gpt-5.6-terra",
            ["chat_audit"] = "gpt-5.6-sol",
        });
        Assert.Equal("gpt-5.6-sol", factory.ResolveDeployment(requested));
    }

    [Fact]
    public void An_alias_nobody_configured_still_falls_back_as_before()
    {
        var factory = Factory(new Dictionary<string, string> { ["chat"] = "gpt-5.6-terra" });
        // chat-audit is deliberately allowed to degrade to chat when truly unset.
        Assert.Equal("gpt-5.6-terra", factory.ResolveDeployment("chat-audit"));
        Assert.Null(factory.ResolveDeployment("nothing-like-this"));
    }

    private static FoundryClientFactory Factory(Dictionary<string, string> models) =>
        new(new NoSecrets(), Options.Create(new AiOptions { Models = models }));

    private sealed class NoSecrets : Castmill.Api.Services.Secrets.IUserSecretsService
    {
        public Task SetAsync(Guid userId, Castmill.Api.Services.Secrets.SecretKind kind, string value, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetAsync(Guid userId, Castmill.Api.Services.Secrets.SecretKind kind, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> RemoveAsync(Guid userId, Castmill.Api.Services.Secrets.SecretKind kind, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<Castmill.Api.Services.Secrets.SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Castmill.Api.Services.Secrets.SecretKind, DateTimeOffset>>(
                new Dictionary<Castmill.Api.Services.Secrets.SecretKind, DateTimeOffset>());
    }
}
