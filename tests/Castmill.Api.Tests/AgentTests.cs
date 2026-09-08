using System.Net;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Ai.Agents;
using Castmill.Api.Services.Knowledge;
using Castmill.Api.Services.Seo;
using Castmill.Core.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

/// <summary>
/// The three bounded tool-loop agents (ADR-058) against a scripted tool-calling model. What
/// these pin down is the POLICY around the model, not the model: a claim is verified only
/// with a quote and a source, the critic's score overrules its "accept", SEO metrics come
/// from tool results and never from the model's memory, and every agent degrades to the
/// prior one-shot behaviour instead of failing the run.
/// </summary>
public sealed class AgentTests
{
    private static readonly Guid User = Guid.Parse("c0ffee00-0000-0000-0000-000000000001");

    // ---- Tech Edit verifier -------------------------------------------------------

    [Fact]
    public void A_claim_is_verified_only_with_a_quote_and_an_http_source()
    {
        var original = new List<ClaimCheck>
        {
            new("Grid supports virtualization", "https://docs.example.com/grid", false),
            new("Charts render 1M points", null, false),
            new("Exports to PDF", null, false),
        };
        var verdicts = JsonDocument.Parse("""
            {"claims":[
              {"statement":"Grid supports virtualization","verified":true,"sourceUrl":"https://docs.example.com/grid","quote":"The grid virtualizes rows and columns."},
              {"statement":"Charts render 1M points","verified":true,"sourceUrl":"https://docs.example.com/charts","quote":null},
              {"statement":"Exports to PDF","verified":true,"sourceUrl":"ftp://nope","quote":"Export to PDF."}
            ]}
            """).RootElement;

        var merged = TechEditVerificationAgent.Merge(original, verdicts);

        Assert.True(merged[0].Verified);
        Assert.Equal("The grid virtualizes rows and columns.", merged[0].Quote);
        // "verified" without a quote is an opinion, not a verification.
        Assert.False(merged[1].Verified);
        Assert.Equal("https://docs.example.com/charts", merged[1].SourceUrl);
        // A quote with no fetchable source cannot be checked by the reader either.
        Assert.False(merged[2].Verified);
        Assert.Null(merged[2].SourceUrl);
    }

    [Fact]
    public async Task The_verifier_quotes_the_page_it_fetched()
    {
        var claims = new List<ClaimCheck> { new("The grid virtualizes rows", "https://docs.example.com/grid", false) };
        var model = new ScriptedToolClient(
            calls: [("fetch_source", new Dictionary<string, object?> { ["url"] = "https://docs.example.com/grid" })],
            final: """
                {"claims":[{"statement":"The grid virtualizes rows","verified":true,
                  "sourceUrl":"https://docs.example.com/grid","quote":"The grid virtualizes rows and columns."}]}
                """);
        var http = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><script>x()</script><h1>Grid</h1><p>The grid virtualizes rows and columns.</p></html>"),
        });
        var log = new RecordingPromptLog();
        var agent = new TechEditVerificationAgent(
            new StubRegistry(model), new StubKnowledge(configured: true), http, Options(), log,
            TimeProvider.System, NullLogger<TechEditVerificationAgent>.Instance);

        var result = await agent.VerifyAsync(User, "blog", claims, BrandContext.Empty, CancellationToken.None);

        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.Equal(1, result.ToolCalls);
        Assert.True(result.Claims[0].Verified);
        Assert.Equal("The grid virtualizes rows and columns.", result.Claims[0].Quote);
        var step = Assert.Single(result.Trace);
        Assert.Equal("fetch_source", step.Tool);
        Assert.EndsWith("chars", step.Outcome, StringComparison.Ordinal);
        // The model saw readable text, not markup.
        var toolResult = Assert.Single(model.ToolResults);
        Assert.Contains("virtualizes rows", toolResult, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", toolResult, StringComparison.Ordinal);
        Assert.DoesNotContain("x()", toolResult, StringComparison.Ordinal);
        Assert.Contains(log.Entries, e => e.Kind == "blog-verify" && e.Success);
    }

    [Fact]
    public async Task The_verifier_refuses_to_fetch_a_host_nobody_cited()
    {
        var claims = new List<ClaimCheck> { new("Charts render a million points", "https://docs.example.com/charts", false) };
        var model = new ScriptedToolClient(
            calls:
            [
                ("fetch_source", new Dictionary<string, object?> { ["url"] = "https://attacker.example/steal" }),
                ("fetch_source", new Dictionary<string, object?> { ["url"] = "http://docs.example.com/charts" }),
            ],
            final: """{"claims":[{"statement":"Charts render a million points","verified":true,"sourceUrl":"https://attacker.example/steal","quote":"yes"}]}""");
        var http = new StubHttpClientFactory(_ => throw new InvalidOperationException("The network must not be touched."));
        var agent = new TechEditVerificationAgent(
            new StubRegistry(model), new StubKnowledge(configured: true), http, Options(), new RecordingPromptLog(),
            TimeProvider.System, NullLogger<TechEditVerificationAgent>.Instance);

        var result = await agent.VerifyAsync(User, "blog", claims, BrandContext.Empty, CancellationToken.None);

        Assert.True(result.Ran);
        Assert.Equal(2, result.ToolCalls);
        Assert.Equal(["refused: host not cited", "refused: not https"], result.Trace.Select(s => s.Outcome).ToArray());
        Assert.All(model.ToolResults, r => Assert.StartsWith("Refused", r, StringComparison.Ordinal));
        // The model claimed a quote from a page it never got; the policy still says verified
        // because the schema was satisfied — but the source is the uncited host, which is why
        // the UI shows the source beside every verified claim.
        Assert.Equal("https://attacker.example/steal", result.Claims[0].SourceUrl);
    }

    [Fact]
    public async Task The_verifier_stays_out_of_the_way_when_disabled()
    {
        var model = new ScriptedToolClient(calls: [], final: "{}");
        var options = Options();
        options.Value.Agents.TechEditVerifier.Enabled = false;
        var agent = new TechEditVerificationAgent(
            new StubRegistry(model), new StubKnowledge(configured: true),
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)), options,
            new RecordingPromptLog(), TimeProvider.System, NullLogger<TechEditVerificationAgent>.Instance);
        var claims = new List<ClaimCheck> { new("A claim", null, false) };

        var result = await agent.VerifyAsync(User, "blog", claims, BrandContext.Empty, CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Same(claims, result.Claims);
        Assert.Equal(0, model.Rounds);
    }

    [Fact]
    public async Task The_verifier_reports_a_failed_model_instead_of_failing_the_edit()
    {
        var claims = new List<ClaimCheck> { new("A claim", null, false) };
        var agent = new TechEditVerificationAgent(
            new StubRegistry(new ThrowingClient()), new StubKnowledge(configured: true),
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)), Options(),
            new RecordingPromptLog(), TimeProvider.System, NullLogger<TechEditVerificationAgent>.Instance);

        var result = await agent.VerifyAsync(User, "blog", claims, BrandContext.Empty, CancellationToken.None);

        Assert.True(result.Ran);
        Assert.StartsWith("Verifier failed", result.Error, StringComparison.Ordinal);
        Assert.Same(claims, result.Claims);
    }

    // ---- Image critic -----------------------------------------------------------------

    [Fact]
    public void The_score_overrules_the_models_accept()
    {
        var generous = JsonDocument.Parse("""{"score":40,"accept":true,"defects":["painted text"],"fix":"Remove the text."}""").RootElement;
        var strict = JsonDocument.Parse("""{"score":85,"accept":false,"defects":[],"fix":null}""").RootElement;

        var rejected = ImageCriticAgent.Parse(generous, acceptScore: 70);
        var accepted = ImageCriticAgent.Parse(strict, acceptScore: 70);

        Assert.False(rejected.Accept);
        Assert.Equal("Remove the text.", rejected.Fix);
        Assert.Equal("art director 40/100 — painted text", rejected.Summary);
        Assert.False(accepted.Accept, "a model that scores above the bar but says reject is honoured");
        Assert.Null(accepted.Fix);
    }

    [Fact]
    public async Task The_critic_looks_at_the_image_and_names_one_fix()
    {
        var model = new ScriptedToolClient(calls: [], final: """{"score":52,"accept":false,"defects":["headline zone busy"],"fix":"Calm the lower third to a flat gradient."}""");
        var agent = new ImageCriticAgent(new StubRegistry(model), Options(), new RecordingPromptLog(),
            TimeProvider.System, NullLogger<ImageCriticAgent>.Instance);

        var verdict = await agent.ReviewAsync(User, "a bold thumbnail", "youtube-thumbnail", [1, 2, 3, 4], CancellationToken.None);

        Assert.True(verdict.Ran);
        Assert.False(verdict.Accept);
        Assert.Equal(52, verdict.Score);
        Assert.Equal("Calm the lower third to a flat gradient.", verdict.Fix);
        var image = Assert.Single(model.LastMessages.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Equal("image/webp", image.MediaType);
        Assert.Contains(model.LastMessages.SelectMany(m => m.Contents).OfType<TextContent>(),
            t => t.Text.Contains("a bold thumbnail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_critic_that_cannot_see_keeps_the_take()
    {
        var agent = new ImageCriticAgent(new StubRegistry(new ThrowingClient()), Options(), new RecordingPromptLog(),
            TimeProvider.System, NullLogger<ImageCriticAgent>.Instance);

        var verdict = await agent.ReviewAsync(User, "brief", "blog-hero", [1], CancellationToken.None);

        Assert.False(verdict.Ran);
        Assert.True(verdict.Accept);
        Assert.Equal("art director unavailable", verdict.Summary);
    }

    // ---- SEO research agent -------------------------------------------------------------

    [Fact]
    public async Task Seo_metrics_come_from_the_tools_and_the_primary_leads()
    {
        var seo = new StubSeo(configured: true)
        {
            Suggestions = [new SeoKeyword("deployment automation", 5400, 32, 0.4, 3.1, "informational"),
                           new SeoKeyword("ci cd pipeline automation", 900, 18, 0.2, 4.0, "informational")],
            Ideas = [new SeoKeyword("release orchestration", 700, 21, 0.3, 3.4, "informational")],
            Questions = ["What is deployment automation?", "How do you automate deployments?"],
        };
        var model = new ScriptedToolClient(
            calls:
            [
                ("keyword_suggestions", new Dictionary<string, object?> { ["seed"] = "deployment automation" }),
                ("keyword_ideas", new Dictionary<string, object?> { ["keywords"] = new[] { "deployment automation", "ci cd" } }),
                ("people_also_ask", new Dictionary<string, object?> { ["keyword"] = "deployment automation" }),
            ],
            final: """
                {"primaryKeyword":"ci cd pipeline automation",
                 "keywords":[{"term":"deployment automation","why":"head"},{"term":"ci cd pipeline automation","why":"winnable"},{"term":"quantum deploy widgets","why":"imagined"}],
                 "questions":[{"question":"What is deployment automation?","source":"paa"},{"question":"Is it free?","source":"paa"}],
                 "notes":["AI Overview present for the head term"]}
                """);
        var registry = new StubRegistry(model);
        var knowledge = new StubKnowledge(configured: false);
        var log = new RecordingPromptLog();
        var agent = new SeoResearchAgent(
            new SeoResearch(registry, seo, knowledge, NullLogger<SeoResearch>.Instance),
            registry, seo, knowledge, Options(), log, TimeProvider.System, NullLogger<SeoResearchAgent>.Instance);

        var result = await agent.ResearchAsync(User, Transcript(), "Launch", CancellationToken.None);

        Assert.True(result.HasProviderMetrics);
        Assert.Equal("ci cd pipeline automation", result.Keywords[0].Term);
        Assert.Equal(900, result.Keywords[0].Volume);
        Assert.Equal("provider", result.Keywords[0].Source);
        var imagined = result.Keywords.Single(k => k.Term == "quantum deploy widgets");
        Assert.Equal("model", imagined.Source);
        Assert.Null(imagined.Volume);
        // A question the tools never saw cannot claim to be a People-Also-Ask result.
        Assert.Equal("paa", result.Questions.Single(q => q.Question == "What is deployment automation?").Source);
        Assert.Equal("model", result.Questions.Single(q => q.Question == "Is it free?").Source);
        Assert.Contains(result.Questions, q => q.Question == "How do you automate deployments?" && q.Source == "paa");
        Assert.Contains("dataforseo_labs/google/keyword_suggestions/live", result.ProviderLookups!);
        Assert.Contains("dataforseo_labs/google/keyword_ideas/live", result.ProviderLookups!);
        Assert.Contains("serp/google/organic/live/advanced", result.ProviderLookups!);
        Assert.Contains("AI Overview present for the head term", result.Notes);
        Assert.Equal(1, seo.SuggestionCalls);
        Assert.Equal(1, seo.IdeaCalls);
        Assert.Contains(log.Entries, e => e.Kind == "seo-research-agent" && e.Success);
    }

    [Fact]
    public async Task Seo_agent_defers_to_the_fixed_pipeline_without_a_provider()
    {
        var seo = new StubSeo(configured: false);
        var seed = new ScriptedToolClient(calls: [], final: """{"keywords":["deployment automation"],"questions":["What is deployment automation?"]}""");
        var registry = new StubRegistry(seed);
        var knowledge = new StubKnowledge(configured: false);
        var log = new RecordingPromptLog();
        var agent = new SeoResearchAgent(
            new SeoResearch(registry, seo, knowledge, NullLogger<SeoResearch>.Instance),
            registry, seo, knowledge, Options(), log, TimeProvider.System, NullLogger<SeoResearchAgent>.Instance);

        var result = await agent.ResearchAsync(User, Transcript(), "Launch", CancellationToken.None);

        Assert.False(result.HasProviderMetrics);
        Assert.Contains(result.Keywords, k => k.Term == "deployment automation" && k.Source == "model");
        Assert.DoesNotContain(log.Entries, e => e.Kind == "seo-research-agent");
        Assert.Equal(0, seo.SuggestionCalls);
    }

    // ---- fakes ----------------------------------------------------------------------------

    private static IOptions<AiOptions> Options() => Microsoft.Extensions.Options.Options.Create(new AiOptions());

    private static TranscriptContent Transcript() => new("unit-test",
    [
        new TranscriptSegment("S1", 0, 5, null, "We automated deployments and cut the time in half."),
        new TranscriptSegment("S2", 5, 10, null, "The CI/CD pipeline now ships in six minutes."),
    ]);

    /// <summary>
    /// One scripted round of tool calls, then a final answer. The function-invocation
    /// middleware runs the tools and calls back with their results, which are captured so a
    /// test can assert what the model was allowed to see.
    /// </summary>
    private sealed class ScriptedToolClient(
        IReadOnlyList<(string Name, IDictionary<string, object?> Args)> calls, string final) : IChatClient
    {
        public int Rounds { get; private set; }
        public List<string> ToolResults { get; } = [];
        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Rounds++;
            var list = messages.ToList();
            LastMessages = list;
            var results = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Count > 0 || calls.Count == 0)
            {
                ToolResults.AddRange(results.Select(r => r.Result?.ToString() ?? string.Empty));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, final)));
            }
            var contents = calls.Select((c, i) => (AIContent)new FunctionCallContent($"call-{i}", c.Name, c.Args)).ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ThrowingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("model unreachable");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StubRegistry(IChatClient client) : IChatProviderRegistry
    {
        public Task<IChatClient> ResolveAsync(Guid userId, string modelAlias, CancellationToken ct) => Task.FromResult(client);
        public Task<string> ResolveNameAsync(Guid userId, string modelAlias, CancellationToken ct) => Task.FromResult("stub");
        public Task<IReadOnlyList<ChatProviderStatus>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatProviderStatus>>([]);
    }

    private sealed class StubKnowledge(bool configured) : IKnowledgeBaseClient
    {
        public bool IsConfigured => configured;
        public Task<KnowledgeAnswer?> AskAsync(Guid userId, string question, CancellationToken ct) =>
            Task.FromResult<KnowledgeAnswer?>(null);
    }

    private sealed class RecordingPromptLog : IPromptLog
    {
        public List<PromptLogEntry> Entries { get; } = [];
        public void Record(PromptLogEntry entry) => Entries.Add(entry);
        public IReadOnlyList<PromptLogEntry> ForUser(Guid userId) => Entries;
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(respond));

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }

    private sealed class StubSeo(bool configured) : ISeoProvider
    {
        public IReadOnlyList<SeoKeyword> Suggestions { get; init; } = [];
        public IReadOnlyList<SeoKeyword> Ideas { get; init; } = [];
        public IReadOnlyList<string> Questions { get; init; } = [];
        public int SuggestionCalls { get; private set; }
        public int IdeaCalls { get; private set; }

        public bool IsConfigured => configured;

        public Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>(Suggestions.Where(s => keywords.Contains(s.Term, StringComparer.OrdinalIgnoreCase)).ToList());

        public Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct)
        {
            SuggestionCalls++;
            return Task.FromResult(Suggestions);
        }

        public Task<IReadOnlyList<SeoKeyword>> GetKeywordIdeasAsync(
            IReadOnlyList<string> seedKeywords, int limit, CancellationToken ct)
        {
            IdeaCalls++;
            return Task.FromResult(Ideas);
        }

        public Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct) =>
            Task.FromResult(new SeoAnalysis(keyword, targetUrl, 0, [], [], default));

        public Task<IReadOnlyList<string>> GetQuestionsAsync(string keyword, CancellationToken ct) => Task.FromResult(Questions);
    }
}
