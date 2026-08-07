using System.Net;
using System.Text;
using System.Text.Json;
using Castmill.Api.Services.Knowledge;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

/// <summary>
/// The knowledge gateway the Tech Edit consults (ADR-020). It answers with a synthesised
/// answer plus citations rather than raw chunks, so what matters here is that the envelope is
/// read forgivingly, the bearer token reaches it, and no failure path ever echoes that token.
/// </summary>
public sealed class KnowledgeBaseTests
{
    private const string RealWorldEnvelope = """
        {
          "output": "Here are the recent Reveal blog posts:\n\n1. Reveal 2.0: Built for How You Actually Build Today (June 4, 2026).",
          "title": "Recent Reveal Blog Posts",
          "citations": [
            { "url": "https://www.revealbi.io/blog/ai-powered-analytics", "title": "AI-Powered Analytics" },
            { "url": "https://www.revealbi.io/blog/reveal-2-0-release", "title": "Reveal 2.0" }
          ],
          "confidenceLevel": 79,
          "confidenceScores": { "evaluationMode": "list-coverage", "retrievalRelevance": 75 },
          "suggestions": [
            "What are the key features in Reveal 2.0?",
            "How does Reveal handle data security?"
          ]
        }
        """;

    [Fact]
    public async Task The_gateway_envelope_parses_into_an_answer_with_its_sources()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, RealWorldEnvelope));
        var answer = await Client(handler, token: "kb-token")
            .AskAsync(Guid.NewGuid(), "Reveal security", TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.Equal("Recent Reveal Blog Posts", answer.Title);
        Assert.Equal(79, answer.ConfidenceLevel);
        Assert.Equal(2, answer.Citations.Count);
        Assert.Equal("https://www.revealbi.io/blog/reveal-2-0-release", answer.Citations[1].Url);
        // suggestions are real questions people ask — the raw material for FAQ/AEO work.
        Assert.Equal(2, answer.Suggestions.Count);
    }

    [Fact]
    public async Task The_prompt_block_carries_the_answer_and_its_source_urls()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, RealWorldEnvelope));
        var answer = await Client(handler, token: "kb-token")
            .AskAsync(Guid.NewGuid(), "Reveal security", TestContext.Current.CancellationToken);

        var block = answer!.ToPromptBlock();
        Assert.Contains("Recent Reveal Blog Posts", block, StringComparison.Ordinal);
        Assert.Contains("Reveal 2.0: Built for How You Actually Build Today", block, StringComparison.Ordinal);
        Assert.Contains("https://www.revealbi.io/blog/reveal-2-0-release", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bearer_token_is_sent_and_the_question_rides_the_configured_field()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, RealWorldEnvelope));
        await Client(handler, token: "kb-token")
            .AskAsync(Guid.NewGuid(), "Reveal security", TestContext.Current.CancellationToken);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("kb-token", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("\"query\":\"Reveal security\"", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A gateway error can quote the request back, and the request travelled with a bearer
    /// token — so a failure returns null and the pass runs without the block.
    /// </summary>
    [Fact]
    public async Task A_gateway_error_yields_no_answer_and_never_surfaces_the_token()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.Unauthorized, """{"error":"bad token kb-token"}"""));

        var answer = await Client(handler, token: "kb-token")
            .AskAsync(Guid.NewGuid(), "Reveal security", TestContext.Current.CancellationToken);

        Assert.Null(answer);
    }

    [Fact]
    public async Task Malformed_or_empty_payloads_are_treated_as_no_answer()
    {
        foreach (var body in new[] { "not json at all", "{}", """{"output":""}""", "[]" })
        {
            var handler = new StubHandler(_ => (HttpStatusCode.OK, body));
            Assert.Null(await Client(handler, token: "kb-token")
                .AskAsync(Guid.NewGuid(), "q", TestContext.Current.CancellationToken));
        }
    }

    /// <summary>No stored token means the gateway is never called at all.</summary>
    [Fact]
    public async Task Without_a_stored_token_the_gateway_is_not_contacted()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, RealWorldEnvelope));

        Assert.Null(await Client(handler, token: null)
            .AskAsync(Guid.NewGuid(), "q", TestContext.Current.CancellationToken));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task An_unconfigured_gateway_is_not_ready_and_is_not_contacted()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, RealWorldEnvelope));
        var client = Client(handler, token: "kb-token", baseUrl: "");

        Assert.False(client.IsConfigured);
        Assert.Null(await client.AskAsync(Guid.NewGuid(), "q", TestContext.Current.CancellationToken));
        Assert.Null(handler.LastRequest);
    }

    /// <summary>Unknown fields are ignored, so a gateway that grows its envelope keeps working.</summary>
    [Fact]
    public void Unknown_envelope_fields_do_not_break_parsing()
    {
        using var doc = JsonDocument.Parse(
            """{"output":"text","somethingNew":{"a":1},"citations":[{"url":"https://x.test"}]}""");

        var answer = KnowledgeBaseClient.Parse(doc.RootElement);

        Assert.NotNull(answer);
        Assert.Equal("text", answer.Output);
        Assert.Equal("https://x.test", Assert.Single(answer.Citations).Url);
        Assert.Null(answer.ConfidenceLevel);
    }

    // ---- helpers ---------------------------------------------------------------

    private static KnowledgeBaseClient Client(
        StubHandler handler, string? token, string baseUrl = "https://ai-agent-gateway.test") =>
        new(new StubFactory(handler),
            new StubSecrets(token),
            Options.Create(new KnowledgeBaseOptions { BaseUrl = baseUrl }),
            NullLogger<KnowledgeBaseClient>.Instance);

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            var (status, body) = respond(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubSecrets(string? token) : IUserSecretsService
    {
        public Task SetAsync(Guid userId, SecretKind kind, string value, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> GetAsync(Guid userId, SecretKind kind, CancellationToken ct) =>
            Task.FromResult(kind == SecretKind.KnowledgeBaseToken ? token : null);

        public Task<bool> RemoveAsync(Guid userId, SecretKind kind, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyDictionary<SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<SecretKind, DateTimeOffset>>(new Dictionary<SecretKind, DateTimeOffset>());
    }
}
