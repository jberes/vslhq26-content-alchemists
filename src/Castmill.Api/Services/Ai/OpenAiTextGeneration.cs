using System.ClientModel;
using Castmill.Api.Data;
using Castmill.Api.Services.Secrets;
using Castmill.Core.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

// Background responses (MEAI001) and the Responses request options (OPENAI001) are marked
// experimental by their SDKs; they are the only way to run a long reasoning call to completion.
#pragma warning disable MEAI001, OPENAI001

namespace Castmill.Api.Services.Ai;

/// <summary>Which model writes this producer's text (ADR-086).</summary>
public interface ITextGenerationRoute
{
    Task<TextGenerationChoice> ChoiceAsync(Guid userId, CancellationToken ct);
}

public sealed class TextGenerationRoute(CastmillDbContext db) : ITextGenerationRoute, IDisposable
{
    private readonly Dictionary<Guid, TextGenerationChoice> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TextGenerationChoice> ChoiceAsync(Guid userId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(userId, out var cached))
            {
                return cached;
            }

            // By user id rather than through the tenant filter: a run that outlives its request
            // still belongs to the producer who pressed it, and must never quietly fall back to
            // a different model because no tenant claim is in scope.
            var rows = await db.UserSettings.IgnoreQueryFilters()
                .Where(s => s.UserId == userId && !s.IsEncrypted && s.Key.StartsWith("ai.text."))
                .Select(s => new { s.Key, s.Value })
                .ToListAsync(ct);
            var choice = TextGenerationSettings.Read(rows.Select(r => KeyValuePair.Create(r.Key, r.Value)));
            _cache[userId] = choice;
            return choice;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

public interface IOpenAiTextClientFactory
{
    Task<string?> KeyAsync(Guid userId, CancellationToken ct);
    Task<IChatClient> CreateAsync(Guid userId, TextGenerationChoice choice, CancellationToken ct);
}

public sealed class OpenAiTextClientFactory(
    IUserSecretsService secrets,
    IOptions<AiOptions> options,
    TimeProvider clock) : IOpenAiTextClientFactory
{
    public const string ProviderName = "openai";

    public async Task<string?> KeyAsync(Guid userId, CancellationToken ct) =>
        await secrets.GetAsync(userId, SecretKind.OpenAiImageKey, ct) is { Length: > 0 } stored
            ? stored
            : options.Value.OpenAI.ApiKey is { Length: > 0 } configured ? configured : null;

    public async Task<IChatClient> CreateAsync(Guid userId, TextGenerationChoice choice, CancellationToken ct)
    {
        var key = await KeyAsync(userId, ct) ?? throw new AiNotConfiguredException(
            "Text generation is set to OpenAI, but no OpenAI API key is stored. Add it under "
            + "Settings → Credentials, or switch text generation back to Foundry.");

        // The SDK owns its transport, deliberately not IHttpClientFactory: the standard
        // resilience handler's ~30 s attempt timeout and retries would cut or re-bill a call.
        // Each request here is short anyway — a background create, then polls.
        var client = new ResponsesClient(
            new ApiKeyCredential(key),
            new ResponsesClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) });
        return new BackgroundReasoningChatClient(client.AsIChatClient(choice.Model), choice, clock);
    }
}

/// <summary>
/// Runs every request as an OpenAI background response at the producer's effort and output
/// ceiling, polled to completion. At "max" effort one call can think for many minutes, and a
/// request held open that long with no bytes moving is cut by idle-connection limits between
/// App Service and OpenAI; a background job plus short polls is not. Call sites — tool-calling
/// agents included — see an ordinary <see cref="IChatClient"/>.
/// </summary>
public sealed class BackgroundReasoningChatClient(
    IChatClient inner,
    TextGenerationChoice choice,
    TimeProvider clock,
    TimeSpan? pollInterval = null) : DelegatingChatClient(inner)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);

    /// <summary>One call's ceiling. The run-level cap bounds the whole fan-out separately.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(60);

    public TextGenerationChoice Choice => choice;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = Configure(options, background: true);
        var response = await base.GetResponseAsync(messages, request, cancellationToken);
        var deadline = clock.GetUtcNow() + MaxWait;
        while (response.ContinuationToken is { } token)
        {
            if (clock.GetUtcNow() > deadline)
            {
                throw new TimeoutException(
                    $"{choice.Model} did not finish within {MaxWait.TotalMinutes:0} minutes.");
            }
            await Task.Delay(_pollInterval, clock, cancellationToken);
            var poll = request.Clone();
            poll.ContinuationToken = token;
            response = await base.GetResponseAsync([], poll, cancellationToken);
        }
        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Configure(options, background: false), cancellationToken);

    internal ChatOptions Configure(ChatOptions? options, bool background)
    {
        var request = options?.Clone() ?? new ChatOptions();
        // Reasoning models reject sampling knobs; effort is the only quality dial.
        request.Temperature = null;
        request.TopP = null;
        request.TopK = null;
        request.FrequencyPenalty = null;
        request.PresencePenalty = null;
        request.Seed = null;
        request.MaxOutputTokens = choice.MaxOutputTokens;
        request.AllowBackgroundResponses = background;

        var callerFactory = request.RawRepresentationFactory;
        request.RawRepresentationFactory = client =>
        {
            var raw = callerFactory?.Invoke(client) as CreateResponseOptions ?? new CreateResponseOptions();
            raw.ReasoningOptions ??= new ResponseReasoningOptions();
            raw.ReasoningOptions.ReasoningEffortLevel = new ResponseReasoningEffortLevel(choice.Effort);
            // A background response exists only if it is stored.
            raw.StoredOutputEnabled = true;
            return raw;
        };
        return request;
    }
}
