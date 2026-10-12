using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Secrets;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

#pragma warning disable MEAI001, OPENAI001

namespace Castmill.Api.Tests;

/// <summary>
/// ADR-086: a producer who chooses OpenAI for text gets that one model for EVERY text call —
/// drafts, audits, the Tech Edit, briefs, agents — at the highest effort and output ceiling,
/// and never a silent fall back to Foundry.
/// </summary>
public sealed class OpenAiTextRouteTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly string[] EveryAlias = ["chat", "chat-audit", FoundryClientFactory.TechEditAlias];

    [Fact]
    public void Unset_settings_mean_foundry_and_the_highest_effort_and_ceiling_for_openai()
    {
        var choice = TextGenerationSettings.Read([]);

        Assert.Equal(TextGenerationSettings.Foundry, choice.Provider);
        Assert.Equal("gpt-6.1-sol", choice.Model);
        Assert.Equal("max", choice.Effort);
        Assert.Equal(128_000, choice.MaxOutputTokens);
    }

    [Fact]
    public void Stored_settings_are_read_and_nonsense_falls_back_to_the_best_values()
    {
        var chosen = TextGenerationSettings.Read([
            KeyValuePair.Create(TextGenerationSettings.ProviderKey, "OpenAI"),
            KeyValuePair.Create(TextGenerationSettings.ModelKey, " gpt-6.1-terra "),
            KeyValuePair.Create(TextGenerationSettings.EffortKey, "xhigh"),
            KeyValuePair.Create(TextGenerationSettings.MaxOutputTokensKey, "64000"),
        ]);
        Assert.Equal(new TextGenerationChoice("openai", "gpt-6.1-terra", "xhigh", 64_000), chosen);

        var nonsense = TextGenerationSettings.Read([
            KeyValuePair.Create(TextGenerationSettings.ProviderKey, "openai"),
            KeyValuePair.Create(TextGenerationSettings.EffortKey, "ludicrous"),
            KeyValuePair.Create(TextGenerationSettings.MaxOutputTokensKey, "12"),
        ]);
        Assert.Equal("max", nonsense.Effort);
        Assert.Equal(128_000, nonsense.MaxOutputTokens);
    }

    [Fact]
    public async Task When_openai_is_chosen_every_alias_resolves_to_that_model()
    {
        var registry = Registry(OpenAiChoice(), openAiKey: "sk-test");
        var ct = TestContext.Current.CancellationToken;

        foreach (var alias in EveryAlias)
        {
            var client = Assert.IsType<BackgroundReasoningChatClient>(await registry.ResolveAsync(User, alias, ct));
            Assert.Equal("gpt-6.1-sol", client.Choice.Model);
            Assert.Equal("openai", await registry.ResolveNameAsync(User, alias, ct));
        }
    }

    [Fact]
    public async Task Openai_without_a_key_is_an_error_never_a_quiet_foundry_fallback()
    {
        var registry = Registry(OpenAiChoice(), openAiKey: null);

        var ex = await Assert.ThrowsAsync<AiNotConfiguredException>(() =>
            registry.ResolveAsync(User, "chat", TestContext.Current.CancellationToken));
        Assert.Contains("no OpenAI API key", ex.Message, StringComparison.Ordinal);

        var status = await registry.TextGenerationStatusAsync(User, TestContext.Current.CancellationToken);
        Assert.False(status.Ready);
        Assert.Equal("openai", status.Provider);
    }

    [Fact]
    public async Task Foundry_remains_the_route_when_openai_is_not_chosen()
    {
        var registry = Registry(TextGenerationSettings.Read([]), openAiKey: "sk-test");

        Assert.Equal("foundry", await registry.ResolveNameAsync(User, "chat", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_request_asks_for_the_chosen_effort_ceiling_and_a_stored_background_response()
    {
        var client = new BackgroundReasoningChatClient(new ScriptedChatClient([]), OpenAiChoice(), TimeProvider.System);

        var options = client.Configure(new ChatOptions { Temperature = 0.7f, TopP = 0.9f, MaxOutputTokens = 500 }, background: true);

        Assert.Null(options.Temperature);
        Assert.Null(options.TopP);
        Assert.Equal(128_000, options.MaxOutputTokens);
        Assert.True(options.AllowBackgroundResponses);
        var raw = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(null!));
        Assert.Equal("max", raw.ReasoningOptions.ReasoningEffortLevel.ToString());
        Assert.True(raw.StoredOutputEnabled);
    }

    [Fact]
    public async Task A_background_response_is_polled_until_the_model_finishes()
    {
        var token = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 });
        var inner = new ScriptedChatClient([
            new ChatResponse([]) { ContinuationToken = token },
            new ChatResponse([]) { ContinuationToken = token },
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "finished")),
        ]);
        var client = new BackgroundReasoningChatClient(inner, OpenAiChoice(), TimeProvider.System, TimeSpan.Zero);

        var response = await client.GetResponseAsync("write", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("finished", response.Text);
        Assert.Equal(3, inner.Requests.Count);
        Assert.Null(inner.Requests[0].ContinuationToken);
        Assert.Same(token, inner.Requests[1].ContinuationToken);
        Assert.Same(token, inner.Requests[2].ContinuationToken);
    }

    [Fact]
    public async Task A_job_openai_failed_is_retried_once_and_an_incomplete_one_says_why()
    {
        var failed = Raw("""{"id":"r1","object":"response","created_at":1,"status":"failed","model":"m","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[],"error":{"code":"server_error","message":"The server had an error."}}""");
        var ok = new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        var retried = new ScriptedChatClient([failed, ok]);
        var client = new BackgroundReasoningChatClient(retried, OpenAiChoice(), TimeProvider.System, TimeSpan.Zero);

        Assert.Equal("done", (await client.GetResponseAsync("x", cancellationToken: TestContext.Current.CancellationToken)).Text);
        Assert.Equal(2, retried.Requests.Count);

        var incomplete = Raw("""{"id":"r2","object":"response","created_at":1,"status":"incomplete","model":"m","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[],"incomplete_details":{"reason":"max_output_tokens"}}""");
        var stopped = new BackgroundReasoningChatClient(new ScriptedChatClient([incomplete]), OpenAiChoice(), TimeProvider.System, TimeSpan.Zero);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stopped.GetResponseAsync("x", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("max_output_tokens", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ChatResponse Raw(string json) => new([])
    {
        RawRepresentation = System.ClientModel.Primitives.ModelReaderWriter.Read<ResponseResult>(BinaryData.FromString(json)),
    };

    internal static TextGenerationChoice OpenAiChoice() => TextGenerationSettings.Read([
        KeyValuePair.Create(TextGenerationSettings.ProviderKey, TextGenerationSettings.OpenAi),
    ]);

    private static ChatProviderRegistry Registry(TextGenerationChoice choice, string? openAiKey)
    {
        var options = Options.Create(new AiOptions
        {
            Models = new(StringComparer.OrdinalIgnoreCase) { ["chat"] = "gpt-5.6-terra", ["chat-audit"] = "gpt-5.6-sol" },
        });
        var secrets = new OpenAiKeySecrets(openAiKey);
        return new ChatProviderRegistry(
            new FoundryClientFactory(secrets, options), options, [],
            new FixedTextRoute(choice),
            new OpenAiTextClientFactory(secrets, options, TimeProvider.System));
    }

    private sealed class OpenAiKeySecrets(string? key) : IUserSecretsService
    {
        public Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct) =>
            Task.FromResult(kind == SecretKind.OpenAiImageKey ? key : null);
        public Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<SecretKind, DateTimeOffset>>(new Dictionary<SecretKind, DateTimeOffset>());
    }

    private sealed class ScriptedChatClient(IReadOnlyList<ChatResponse> script) : IChatClient
    {
        public List<ChatOptions> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(options!);
            return Task.FromResult(script[Requests.Count - 1]);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

/// <summary>A fixed text route, for registries built by hand in tests.</summary>
internal sealed class FixedTextRoute(TextGenerationChoice choice) : ITextGenerationRoute
{
    public Task<TextGenerationChoice> ChoiceAsync(Guid userId, CancellationToken ct) => Task.FromResult(choice);
}

/// <summary>
/// End to end through the real endpoints: the producer saves the OpenAI route in Settings and
/// a whole Press Run then prints with every model call on OpenAI — Foundry is never asked.
/// </summary>
[Collection("api")]
public sealed class OpenAiTextRouteApiTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Choosing_openai_in_settings_sends_the_whole_press_run_to_that_model()
    {
        var openAi = new RecordingOpenAiFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => new UnusableFoundryFactory()));
            services.Replace(ServiceDescriptor.Scoped<IOpenAiTextClientFactory>(_ => openAi));
        }));
        var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"openai-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "OpenAI Tester"), ct);
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        (await client.PutAsJsonAsync($"/api/v1/settings/{TextGenerationSettings.ProviderKey}",
            new { value = TextGenerationSettings.OpenAi }, ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/settings/secrets/OpenAiImageKey", new { value = "sk-test-route" }, ct))
            .EnsureSuccessStatusCode();

        var status = await client.GetFromJsonAsync<AiStatusResponse>("/api/v1/ai/status", ct);
        Assert.Equal(new TextGenerationStatus("openai", "gpt-6.1-sol", "max", 128_000, true, null), status!.TextGeneration);

        var campaign = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("OpenAI route", null), ct);
        var campaignId = (await campaign.Content.ReadFromJsonAsync<CampaignResponse>(ct))!.Id;
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched the new product. It cut deployment time in half. Customers love the new dashboard. The team shipped it in six weeks.", source = "unit-test" }, ct);
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<Ingest>(ct))!.TranscriptArtifactId;

        var run = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new { transcriptArtifactId = transcriptId, brief = "Product launch" }, ct);
        Assert.True(run.StatusCode == HttpStatusCode.OK, await run.Content.ReadAsStringAsync(ct));
        var raw = await run.Content.ReadAsStringAsync(ct);
        var body = System.Text.Json.JsonSerializer.Deserialize<FanOut>(raw, Web);
        Assert.True(body!.Failed == 0, raw);
        Assert.True(body.Succeeded > 0);
        Assert.True(openAi.Calls > body.Succeeded, "drafts AND their audit passes go to OpenAI");
        Assert.All(openAi.Choices, choice => Assert.Equal(("gpt-6.1-sol", "max", 128_000),
            (choice.Model, choice.Effort, choice.MaxOutputTokens)));
    }

    /// <summary>
    /// A max-effort piece can think past the client's five-minute stall guard; the heartbeat
    /// keeps the run row moving meanwhile, and leaves a finished run alone.
    /// </summary>
    [Fact]
    public async Task The_heartbeat_moves_a_running_run_and_leaves_a_finished_one_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var running = Guid.NewGuid();
        var finished = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.GenerationRuns.AddRange(
                new Castmill.Core.GenerationRun { Id = running, TenantId = Guid.NewGuid(), CampaignId = Guid.NewGuid(), Status = "Running", ItemsJson = "[]", StartedAt = started, UpdatedAt = started },
                new Castmill.Core.GenerationRun { Id = finished, TenantId = Guid.NewGuid(), CampaignId = Guid.NewGuid(), Status = "Completed", ItemsJson = "[]", StartedAt = started, UpdatedAt = started });
            await db.SaveChangesAsync(ct);
        }

        var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();
        await using (new RunHeartbeat(scopes, running, TimeProvider.System, ct, TimeSpan.FromMilliseconds(50)))
        await using (new RunHeartbeat(scopes, finished, TimeProvider.System, ct, TimeSpan.FromMilliseconds(50)))
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (await UpdatedAtAsync(running) == started && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, ct);
            }
        }

        Assert.True(await UpdatedAtAsync(running) > started, "a running run's heartbeat moves UpdatedAt");
        Assert.Equal(started, await UpdatedAtAsync(finished));

        async Task<DateTimeOffset> UpdatedAtAsync(Guid id)
        {
            await using var db = NewDb();
            return await db.GenerationRuns.IgnoreQueryFilters().Where(r => r.Id == id)
                .Select(r => r.UpdatedAt).SingleAsync(ct);
        }
    }

    private Castmill.Api.Data.CastmillDbContext NewDb()
    {
        var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<Castmill.Api.Data.CastmillDbContext>>();
        return new Castmill.Api.Data.CastmillDbContext(options, new NoTenant());
    }

    private sealed class NoTenant : Castmill.Api.Tenancy.ITenantProvider
    {
        public Guid? TenantId => null;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    private sealed record FanOut(int Succeeded, int Failed);

    private sealed record Ingest(Guid TranscriptArtifactId);

    private sealed class RecordingOpenAiFactory : IOpenAiTextClientFactory
    {
        public int Calls;
        public List<TextGenerationChoice> Choices { get; } = [];

        public Task<string?> KeyAsync(Guid userId, CancellationToken ct) => Task.FromResult<string?>("sk-test-route");

        public Task<IChatClient> CreateAsync(Guid userId, TextGenerationChoice choice, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Choices)
            {
                Choices.Add(choice);
            }
            return Task.FromResult<IChatClient>(new BackgroundReasoningChatClient(
                new AiGenerationTests.FakeChatClient(), choice, TimeProvider.System, TimeSpan.Zero));
        }
    }

    private sealed class UnusableFoundryFactory : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(null);
        public string? ResolveDeployment(string modelAlias) => null;
        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(null);
        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            throw new InvalidOperationException($"Foundry was asked for '{modelAlias}' on the OpenAI route.");
    }
}
