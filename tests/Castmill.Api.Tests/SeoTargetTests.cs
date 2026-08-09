using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Seo;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Castmill.Api.Tests;

/// <summary>
/// Researching keywords BEFORE the fan-out is only worth anything if the chosen targets
/// actually reach the model. The keyword plan already existed — as a report written about
/// content that had already been generated, which steered nothing.
///
/// The load-bearing assertion in this file is <see cref="Chosen_targets_reach_every_generators_prompt"/>.
/// Everything else guards the ways it can silently stop being true.
/// </summary>
[Collection("api")]
public sealed class SeoTargetTests(CastmillApiFactory factory)
{
    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactoryLike app)
    {
        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"seo-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "SEO Tester"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Chosen_targets_reach_every_generators_prompt()
    {
        var capture = new CapturingFoundry();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));

        var client = await AuthedClientAsync(new WebApplicationFactoryLike(app));
        var (campaignId, transcriptId) = await SeedAsync(client);

        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/seo-targets",
            new SeoTargetsRequest(
                "react data grid",
                [new SeoTarget("react data grid", 8100, 42, 157.7, "provider"),
                 new SeoTarget("react table component", 2400, 31, 58.5, "provider")],
                [new SeoQuestion("How do you paginate a React data grid?", "paa")]));

        (await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/newsletter",
            new { transcriptArtifactId = transcriptId })).EnsureSuccessStatusCode();

        var prompt = Assert.Single(capture.Prompts,
            p => p.Contains("newsletter edition", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("PRIMARY keyword: \"react data grid\"", prompt, StringComparison.Ordinal);
        Assert.Contains("react table component", prompt, StringComparison.Ordinal);
        Assert.Contains("How do you paginate a React data grid?", prompt, StringComparison.Ordinal);

        // The AEO rule is the reason the questions are there at all — an answer that only makes
        // sense in place is one an assistant cannot quote.
        Assert.Contains("self-contained sentence", prompt, StringComparison.Ordinal);

        // And the guard against the obvious failure mode of keyword targeting.
        Assert.Contains("Never invent a statistic", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_campaign_with_no_targets_gets_no_target_block()
    {
        var capture = new CapturingFoundry();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));

        var client = await AuthedClientAsync(new WebApplicationFactoryLike(app));
        var (campaignId, transcriptId) = await SeedAsync(client);

        (await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/newsletter",
            new { transcriptArtifactId = transcriptId })).EnsureSuccessStatusCode();

        var prompt = Assert.Single(capture.Prompts,
            p => p.Contains("newsletter edition", StringComparison.OrdinalIgnoreCase));

        // An empty steering heading is worse than none: it invites the model to fill it.
        Assert.DoesNotContain("Search and answer-engine targets", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_gate_refuses_content_before_analysis_and_approval()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Seo:RequireAnalysisBeforeGeneration", "true"));
        var client = await AuthedClientAsync(new WebApplicationFactoryLike(app));
        var (campaignId, transcriptId) = await SeedAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/newsletter",
            new { transcriptArtifactId = transcriptId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("analysis", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Targets_round_trip_and_the_primary_is_always_one_of_the_keywords()
    {
        var client = await AuthedClientAsync(new WebApplicationFactoryLike(factory));
        var (campaignId, _) = await SeedAsync(client);

        // A primary that is not in the list would name a target the rest of the brief never
        // mentions, so the server adds it rather than storing an inconsistent set.
        var saved = await (await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/seo-targets",
            new SeoTargetsRequest("blazor data grid", [new SeoTarget("react data grid", 8100)])))
            .Content.ReadFromJsonAsync<SeoTargetsResponse>();

        Assert.Equal("blazor data grid", saved!.PrimaryKeyword);
        Assert.Contains(saved.Keywords, k => k.Term == "blazor data grid");
        Assert.Contains(saved.Keywords, k => k.Term == "react data grid");

        var reloaded = await client.GetFromJsonAsync<SeoTargetsResponse>(
            $"/api/v1/campaigns/{campaignId}/seo-targets");
        Assert.Equal("blazor data grid", reloaded!.PrimaryKeyword);
        Assert.Equal(2, reloaded.Keywords.Count);
    }

    [Fact]
    public async Task No_primary_supplied_promotes_the_first_keyword()
    {
        var client = await AuthedClientAsync(new WebApplicationFactoryLike(factory));
        var (campaignId, _) = await SeedAsync(client);

        var saved = await (await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/seo-targets",
            new SeoTargetsRequest(null, [new SeoTarget("react data grid", 8100)])))
            .Content.ReadFromJsonAsync<SeoTargetsResponse>();

        Assert.Equal("react data grid", saved!.PrimaryKeyword);
    }

    [Fact]
    public async Task Clearing_the_targets_is_a_real_action()
    {
        var client = await AuthedClientAsync(new WebApplicationFactoryLike(factory));
        var (campaignId, _) = await SeedAsync(client);

        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/seo-targets",
            new SeoTargetsRequest("react data grid", [new SeoTarget("react data grid")]));
        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/seo-targets",
            new SeoTargetsRequest(null, [], []));

        var reloaded = await client.GetFromJsonAsync<SeoTargetsResponse>(
            $"/api/v1/campaigns/{campaignId}/seo-targets");

        Assert.Null(reloaded!.PrimaryKeyword);
        Assert.Empty(reloaded.Keywords);
    }

    /// <summary>
    /// The block is what a writer is actually told. Rendered from the stored shape so a change
    /// to either side that breaks the instruction shows up here rather than in published copy.
    /// </summary>
    [Fact]
    public void The_target_block_reads_as_instructions_not_as_data()
    {
        var block = BrandContextService.BuildSeoTargetBlock(new SeoTargetsResponse(
            "react data grid",
            [new SeoTarget("react data grid"), new SeoTarget("react table component")],
            [new SeoQuestion("How do you paginate a React data grid?", "paa")]))!;

        Assert.Contains("must appear in the title", block, StringComparison.Ordinal);
        Assert.Contains("first 100 words", block, StringComparison.Ordinal);

        // The primary must not be repeated in the secondary list — a writer reading "also use
        // X" about the primary keyword over-uses it, which is exactly what stuffing looks like.
        var secondaryLine = block.Split('\n').Single(l => l.Contains("Secondary keywords", StringComparison.Ordinal));
        Assert.DoesNotContain("react data grid", secondaryLine, StringComparison.Ordinal);
        Assert.Contains("react table component", secondaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_target_set_renders_no_block_at_all()
    {
        Assert.Null(BrandContextService.BuildSeoTargetBlock(new SeoTargetsResponse(null, [], [])));
        Assert.Null(BrandContextService.BuildSeoTargetBlock(null));
    }

    /// <summary>
    /// Google's related searches routinely drop the question mark, so punctuation alone is not
    /// a usable test for "is this a question".
    /// </summary>
    /// <summary>
    /// Verified against the live DataForSEO account: "react data grid" returns NO
    /// people_also_ask block, while "what is a data grid" returns four questions. Research
    /// therefore asks the question form when the plain keyword comes back empty — otherwise
    /// the PAA integration is billed for and silently contributes nothing.
    /// </summary>
    [Fact]
    public async Task People_also_ask_falls_back_to_the_question_form_of_the_keyword()
    {
        var seo = new ScriptedSeo();
        var research = new SeoResearch(
            new StubChat("""{"keywords":["react data grid"],"questions":[]}"""),
            seo,
            new NoKnowledgeBase(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SeoResearch>.Instance);

        var result = await research.ResearchAsync(
            Guid.NewGuid(),
            new Castmill.Core.Ai.TranscriptContent("test",
                [new Castmill.Core.Ai.TranscriptSegment("s01", 0, 3, null, "React data grids.")]),
            "E2E", CancellationToken.None);

        Assert.Equal(["react data grid", "what is react data grid"], seo.QuestionQueries);
        Assert.Contains(result.Questions, q => q.Source == "paa" && q.Question == "What is a data grid?");

        // Ordered by worth, not by the order the sources answered in: the UI pre-selects from
        // the top, so a PAA question appended last is one nobody ever picks.
        Assert.Equal("paa", result.Questions[0].Source);
    }

    [Fact]
    public async Task A_keyword_that_does_have_a_paa_box_costs_only_one_call()
    {
        var seo = new ScriptedSeo { AnswerOnFirstCall = true };
        var research = new SeoResearch(
            new StubChat("""{"keywords":["react data grid"],"questions":[]}"""),
            seo,
            new NoKnowledgeBase(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SeoResearch>.Instance);

        await research.ResearchAsync(
            Guid.NewGuid(),
            new Castmill.Core.Ai.TranscriptContent("test",
                [new Castmill.Core.Ai.TranscriptSegment("s01", 0, 3, null, "React data grids.")]),
            "E2E", CancellationToken.None);

        // The fallback is billed, so it must only fire when the first call found nothing.
        Assert.Single(seo.QuestionQueries);
    }

    [Theory]
    [InlineData("how to build a react data grid", true)]
    [InlineData("what is a data grid", true)]
    [InlineData("can react handle a million rows", true)]
    [InlineData("react data grid?", true)]
    [InlineData("react data grid pricing", false)]
    [InlineData("ignite ui react", false)]
    [InlineData("", false)]
    public void Question_detection_reads_shape_not_punctuation(string text, bool expected) =>
        Assert.Equal(expected, DataForSeoProvider.LooksLikeQuestion(text));

    private static async Task<(Guid CampaignId, Guid TranscriptId)> SeedAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Targeted", null));
        var campaignId = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched. It cut deploy time in half. Everyone was pleased with the result.", source = "test" });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestShape>())!.TranscriptArtifactId;

        return (campaignId, transcriptId);
    }

    private sealed record IngestShape(Guid TranscriptArtifactId, int SegmentCount);

    /// <summary>Thin shim so the same helper works with the factory and a reconfigured app.</summary>
    private sealed class WebApplicationFactoryLike(object app)
    {
        public HttpClient CreateClient() => app switch
        {
            CastmillApiFactory f => f.CreateClient(),
            Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> w => w.CreateClient(),
            _ => throw new InvalidOperationException("Unsupported app type."),
        };
    }

    private sealed class CapturingFoundry : IFoundryClientFactory
    {
        public List<string> Prompts { get; } = [];

        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"), "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new CapturingChat(Prompts));
    }

    private sealed class CapturingChat(List<string> prompts) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            prompts.Add(prompt);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"title":"News","subject":"s","bodyMarkdown":"body","citations":["S1"]}""")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Records which keywords PAA was asked about, and answers only the question form.</summary>
    private sealed class ScriptedSeo : ISeoProvider
    {
        public List<string> QuestionQueries { get; } = [];

        public bool AnswerOnFirstCall { get; init; }

        public bool IsConfigured => true;

        public Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(
            IReadOnlyList<string> keywords, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>(
                [.. keywords.Select(k => new SeoKeyword(k, 8100, 42, 0.5, 3.2))]);

        public Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(
            string seedKeyword, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>([new SeoKeyword(seedKeyword, 8100, 42, 0.5, 3.2)]);

        public Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetQuestionsAsync(string keyword, CancellationToken ct)
        {
            QuestionQueries.Add(keyword);

            var answer = AnswerOnFirstCall || keyword.StartsWith("what is", StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyList<string>>(answer ? ["What is a data grid?"] : []);
        }
    }

    private sealed class StubChat(string json) : IChatProviderRegistry
    {
        public Task<IChatClient> ResolveAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new FixedChat(json));

        public Task<string> ResolveNameAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult("stub");

        public Task<IReadOnlyList<ChatProviderStatus>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatProviderStatus>>([]);
    }

    private sealed class FixedChat(string json) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class NoKnowledgeBase : Castmill.Api.Services.Knowledge.IKnowledgeBaseClient
    {
        public bool IsConfigured => false;

        public Task<Castmill.Api.Services.Knowledge.KnowledgeAnswer?> AskAsync(
            Guid userId, string question, CancellationToken ct) =>
            Task.FromResult<Castmill.Api.Services.Knowledge.KnowledgeAnswer?>(null);
    }
}
