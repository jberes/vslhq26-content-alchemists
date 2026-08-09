using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>
/// Exercises the full B5 orchestration through HTTP with a canned-response
/// model behind the IFoundryClientFactory seam — proving ingest → fan-out →
/// validation → persistence without any real AI spend.
/// </summary>
[Collection("api")]
public sealed class AiGenerationTests(CastmillApiFactory factory)
{
    private WebApplicationFactory<Program> WithFakeModel() =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => new FakeFoundryFactory()))));

    private static async Task<(HttpClient Client, Guid CampaignId, Guid TranscriptId)> SetUpAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"ai-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "AI Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("AI campaign", null));
        var campaignId = (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched the new product. It cut deployment time in half. Customers love the new dashboard. The team shipped it in six weeks.", source = "unit-test" });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;
        return (client, campaignId, transcriptId);
    }

    [Fact]
    public async Task Full_fan_out_generates_validated_artifacts_with_citations()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new { transcriptArtifactId = transcriptId, brief = "Product launch" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("blog;dur=", response.Headers.GetValues("Server-Timing").Single(), StringComparison.Ordinal);

        var body = await response.Content.ReadFromJsonAsync<FanOutResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.Failed);
        // Every fan-out generator, plus blog (which runs its own outline→draft→audit pipeline
        // and so is not in FanOut). Derived rather than hard-coded: a literal here goes stale
        // the moment a generator is added, and reads as a regression rather than a new kind.
        Assert.Equal(Generators.FanOut.Count + 1, body.Succeeded);

        // Every artifact persisted; previews list them all (plus the transcript).
        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        Assert.Equal(Generators.FanOut.Count + 2, previews!.Count);
        Assert.Contains(previews, p => p.Kind == "blog");
        Assert.Contains(previews, p => p.Kind == "social-x");
        Assert.Contains(previews, p => p.Kind == "image-prompts");
    }

    /// <summary>
    /// "Three more LinkedIn posts." Kinds is a SET server-side — repeating the kind in the
    /// array still generates it once — so the count is its own field, and each copy lands as
    /// its own artifact row rather than overwriting the last.
    /// </summary>
    [Fact]
    public async Task A_count_prints_that_many_of_each_requested_kind()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new
            {
                transcriptArtifactId = transcriptId,
                brief = "Angle this at pricing objections",
                kinds = new[] { "social-linkedin" },
                count = 3,
            });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<FanOutResponse>();
        Assert.Equal(3, body!.Succeeded);

        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        var posts = previews!.Where(p => p.Kind == "social-linkedin").ToList();
        Assert.Equal(3, posts.Count);
        // Three distinct rows, not one row saved three times.
        Assert.Equal(3, posts.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task A_count_over_the_cap_is_clamped_rather_than_honoured()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate",
            new { transcriptArtifactId = transcriptId, kinds = new[] { "newsletter" }, count = 500 });

        // Rejected at the boundary by the Range attribute, never reaching the model.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Prompt_log_records_calls_for_the_current_user_only()
    {
        await using var app = WithFakeModel();
        var (client, campaignId, transcriptId) = await SetUpAsync(app);

        await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
            new { transcriptArtifactId = transcriptId });

        var log = await client.GetFromJsonAsync<List<LogEntry>>("/api/v1/ai/log");
        Assert.Contains(log!, e => e.Kind == "social-x" && e.Success);

        // A different user sees an empty log.
        var (stranger, _, _) = await SetUpAsync(app);
        var strangerLog = await stranger.GetFromJsonAsync<List<LogEntry>>("/api/v1/ai/log");
        Assert.DoesNotContain(strangerLog!, e => e.Kind == "social-x");
    }

    [Fact]
    public async Task Status_reports_none_when_no_credentials_configured()
    {
        // Real factory, empty Ai config: credentialSource must be "none".
        var (client, _, _) = await SetUpAsync(factory);
        var status = await client.GetFromJsonAsync<Castmill.Core.Ai.AiStatusResponse>("/api/v1/ai/status");
        Assert.Equal("none", status!.CredentialSource);
        Assert.False(status.EndpointConfigured);
    }

    [Fact]
    public async Task Generate_without_credentials_reports_failures_not_500s()
    {
        var (client, campaignId, transcriptId) = await SetUpAsync(factory);
        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
            new { transcriptArtifactId = transcriptId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Castmill.Core.Ai.GenerationResult>();
        Assert.False(result!.Success);
        Assert.Contains("Foundry", result.Error, StringComparison.Ordinal);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);
    private sealed record FanOutResponse(int Succeeded, int Failed);
    private sealed record LogEntry(string Kind, bool Success);

    // ---- Fakes ---------------------------------------------------------------

    internal sealed class FakeFoundryFactory : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"), "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new FakeChatClient());
    }

    /// <summary>Returns schema-valid canned JSON keyed off distinctive prompt text.</summary>
    internal sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            var json = Respond(prompt);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        private static string Respond(string prompt)
        {
            if (prompt.Contains("Create an outline", StringComparison.Ordinal))
            {
                return """{"title":"Launch story","sections":[{"heading":"Intro","segmentIds":["S1"]}],"citations":["S1","S2"]}""";
            }
            if (prompt.Contains("Write the full blog post", StringComparison.Ordinal))
            {
                var words = string.Join(" ", Enumerable.Repeat("word", 1800));
                return $$"""{"title":"Launch story","markdown":"{{words}} ![stub:blog-hero]()","metaDescription":"d","citations":["S1","S2","S3"]}""";
            }
            if (prompt.Contains("auditing a blog draft", StringComparison.Ordinal))
            {
                return """{"unsupportedClaims":[],"citations":["S1"]}""";
            }
            if (prompt.Contains("nurture sequence", StringComparison.Ordinal))
            {
                return """{"title":"Emails","emails":[{"subject":"a","preview":"p","bodyMarkdown":"b"},{"subject":"b","preview":"p","bodyMarkdown":"b"},{"subject":"c","preview":"p","bodyMarkdown":"b"}],"citations":["S1"]}""";
            }
            if (prompt.Contains("newsletter edition", StringComparison.Ordinal))
            {
                return """{"title":"News","subject":"s","bodyMarkdown":"body","citations":["S2"]}""";
            }
            if (prompt.Contains("landing page copy", StringComparison.Ordinal))
            {
                return """{"title":"Landing","headline":"h","subheadline":"s","sectionsMarkdown":["m"],"cta":"go","citations":["S1"]}""";
            }
            // Keyed on the schema field, not on prose: matching prose is how this fake went
            // stale before, when a prompt was reworded and the generator silently fell through.
            if (prompt.Contains("\"titleVariants\"", StringComparison.Ordinal))
            {
                return """{"title":"Ship it","titleVariants":["a","b","c"],"description":"Line one.\n\nChapters:\n0:00 Intro\n\n{{LINKS}}","chapters":[{"startSeconds":0,"title":"Intro"}],"tags":["react","grid"],"citations":["S1"]}""";
            }
            if (prompt.Contains("show notes", StringComparison.Ordinal))
            {
                return """{"title":"Notes","summaryMarkdown":"s","chapters":[{"startSeconds":0,"title":"Intro"}],"citations":["S1"]}""";
            }
            // Keyed off the schema's field name, not the prose: the clip prompt's wording has
            // already been rewritten once ("short vertical clips" → "vertical short-form
            // clips"), which silently dropped this branch and sank the kind in the fan-out.
            // Field names are the contract, so they are the stable thing to match on.
            if (prompt.Contains("\"platformFit\"", StringComparison.Ordinal))
            {
                // Segment ids, not timestamps — the generator computes in/out from the
                // transcript now, so a fake that returns times would exercise a path the
                // real model no longer takes.
                return """{"title":"Clips","clips":[{"startSegmentId":"S1","endSegmentId":"S3","hook":"h","clipTitle":"Deploy time, halved","description":"d","hashtags":["devops"],"platformFit":["tiktok"],"scores":{"hook":8,"selfContained":7,"payoff":8,"emotion":6}}],"citations":["S2"]}""";
            }
            if (prompt.Contains("Produce an SEO brief", StringComparison.Ordinal))
            {
                return """{"title":"SEO brief","summary":"A launch story about cutting deployment time in half with the new product and dashboard.","focusKeywords":["deployment automation tool","cut deployment time","devops dashboard"],"youtubeTitles":["We Cut Deploy Time in HALF — Here's How","The Dashboard That Halved Our Deployments","Deployment Automation That Actually Works"],"citations":["S2"]}""";
            }
            if (prompt.Contains("image-generation prompts", StringComparison.Ordinal))
            {
                return """{"title":"Images","images":[{"slot":"blog-hero","prompt":"p","aspectRatio":"16:9"},{"slot":"youtube-thumbnail","prompt":"p","aspectRatio":"16:9"},{"slot":"blog-inline-1","prompt":"p","aspectRatio":"4:3"}],"citations":["S3"]}""";
            }
            if (prompt.Contains("\"overlayText\"", StringComparison.Ordinal))
            {
                return """{"title":"Thumbnail directions","concepts":[{"name":"Speed","angle":"faster delivery","prompt":"a fast product launch with negative space","overlayText":"SHIP FASTER","reason":"Matches the deployment intent"},{"name":"Before and after","angle":"transformation","prompt":"split-screen delivery workflow","overlayText":"TIME CUT IN HALF","reason":"Shows the concrete outcome"},{"name":"Dashboard proof","angle":"product evidence","prompt":"product dashboard in an editorial composition","overlayText":"THE PROOF","reason":"Grounds the claim in the product"}],"citations":["S2","S3"]}""";
            }
            // Social posts (each platform prompt says "Write one <platform> post").
            return """{"title":"Post","text":"Short launch post.","hashtags":["#launch"],"citations":["S1"]}""";
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
