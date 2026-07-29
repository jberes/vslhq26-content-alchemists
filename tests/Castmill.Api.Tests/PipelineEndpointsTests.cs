using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Endpoints;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Media;
using Castmill.Api.Services.Publish;
using Castmill.Api.Services.Seo;
using Castmill.Core;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>B6 clip jobs, B7 publish + SEO — through HTTP with faked externals.</summary>
[Collection("api")]
public sealed class PipelineEndpointsTests(CastmillApiFactory factory)
{
    private sealed class CapturingDispatcher : IClipJobDispatcher
    {
        public List<ClipJobMessage> Sent { get; } = [];
        public bool IsConfigured => true;
        public Task EnqueueAsync(ClipJobMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBroker : IPublishBrokerClient
    {
        public Task<IReadOnlyList<BrokerChannel>> ListChannelsAsync(string token, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrokerChannel>>([new BrokerChannel("ch-1", "Main X", "x")]);

        public Task<BrokerPost> SchedulePostAsync(string token, string channelId, string text, DateTimeOffset scheduledAt, string? mediaUrl, CancellationToken ct) =>
            channelId == "ch-bad"
                ? throw new HttpRequestException("boom")
                : Task.FromResult(new BrokerPost($"post-{channelId}", channelId, text, scheduledAt, "scheduled"));

        public Task CancelPostAsync(string token, string postId, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPost>> GetQueueAsync(string token, string channelId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrokerPost>>([]);
    }

    private sealed class FakeSeo : ISeoProvider
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>(
                [.. keywords.Select(k => new SeoKeyword(k, 1200, 0, 0.4, 1.2))]);

        public Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>(
                [new SeoKeyword($"{seedKeyword} tutorial", 5400, 22, 0.3, 0.8)]);

        public Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse("""{"provider":"fake"}""");
            return Task.FromResult(new SeoAnalysis(keyword, targetUrl, 72,
                [new SeoKeyword("castmill", 1200, 34.5, 0.4, 1.2)], ["angle one"], doc.RootElement.Clone()));
        }
    }

    private sealed class FakePublicStore : IPublicContentStore
    {
        public string? LastHtml { get; private set; }
        public bool IsConfigured => true;
        public Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct)
        {
            if (contentType.StartsWith("text/html", StringComparison.Ordinal))
            {
                LastHtml = System.Text.Encoding.UTF8.GetString(bytes.Span);
            }
            return Task.FromResult(new Uri($"https://public.example/{path}"));
        }
    }

    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"pipe-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Pipe Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Clip_job_lifecycle_enqueue_callback_status_and_token_burn()
    {
        var dispatcher = new CapturingDispatcher();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Singleton<IClipJobDispatcher>(dispatcher))));
        var client = await AuthedClientAsync(app);

        var asset = (await (await client.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("video.mp4", "video/mp4", 1000))).Content.ReadFromJsonAsync<AssetResponse>())!;

        var enqueue = await client.PostAsJsonAsync("/api/v1/media/clip-jobs",
            new { assetId = asset.Id, inSeconds = 10.0, outSeconds = 25.0, cropVertical = true, burnCaptions = false });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var message = Assert.Single(dispatcher.Sent);

        // Worker reports progress with the queue-message token.
        var processing = await client.PostAsJsonAsync($"/api/v1/media/clip-jobs/{message.JobId}/callback",
            new { token = message.CallbackToken, status = "Processing" });
        Assert.Equal(HttpStatusCode.NoContent, processing.StatusCode);

        // Wrong token → 401.
        var forged = await client.PostAsJsonAsync($"/api/v1/media/clip-jobs/{message.JobId}/callback",
            new { token = "not-the-token", status = "Succeeded" });
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        var done = await client.PostAsJsonAsync($"/api/v1/media/clip-jobs/{message.JobId}/callback",
            new { token = message.CallbackToken, status = "Succeeded" });
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);

        // Terminal status burned the token: replay is 401.
        var replay = await client.PostAsJsonAsync($"/api/v1/media/clip-jobs/{message.JobId}/callback",
            new { token = message.CallbackToken, status = "Failed" });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var status = await client.GetAsync($"/api/v1/media/clip-jobs/{message.JobId}");
        Assert.Contains("Succeeded", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_fan_out_reports_partial_failures_per_channel()
    {
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Publish:BrokerBaseUrl", "https://broker.example");
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Scoped<IPublishBrokerClient>(_ => new FakeBroker())));
        });
        var client = await AuthedClientAsync(app);

        // Broker token required first — endpoint says so with a 503.
        var without = await client.PostAsJsonAsync("/api/v1/publish/posts",
            new { channelIds = new[] { "ch-1" }, text = "hi", scheduledAt = DateTimeOffset.UtcNow.AddHours(1) });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, without.StatusCode);

        (await client.PutAsJsonAsync("/api/v1/settings/secrets/BrokerToken", new { value = "broker-token-1" }))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/v1/publish/posts", new
        {
            channelIds = new[] { "ch-1", "ch-bad" },
            text = "Launch day!",
            scheduledAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("post-ch-1", body, StringComparison.Ordinal);   // scheduled
        Assert.Contains("ch-bad", body, StringComparison.Ordinal);       // reported failure
        Assert.Contains("HttpRequestException", body, StringComparison.Ordinal);

        var channels = await client.GetAsync("/api/v1/publish/channels");
        Assert.Contains("Main X", await channels.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keyword_plan_chains_ai_brief_into_dataforseo_and_ranks_by_opportunity()
    {
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new FakeSeo()));
            s.Replace(ServiceDescriptor.Scoped<Castmill.Api.Services.Ai.IFoundryClientFactory>(
                _ => new AiGenerationTests.FakeFoundryFactory()));
        }));
        var client = await AuthedClientAsync(app);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Plan campaign", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts",
            new { text = "We launched the new product. It cut deployment time in half. Customers love the new dashboard.", source = "test" });
        using var ingested = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync());
        var transcriptId = ingested.RootElement.GetProperty("transcriptArtifactId").GetGuid();

        var response = await client.PostAsJsonAsync("/api/v1/seo/keyword-plan",
            new { campaignId = campaign.Id, transcriptArtifactId = transcriptId, focus = "rank for deployment automation" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var plan = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Exactly 3 A/B YouTube titles from the AI brief.
        Assert.Equal(3, plan.RootElement.GetProperty("youtubeTitles").GetArrayLength());

        var keywords = plan.RootElement.GetProperty("keywords").EnumerateArray().ToList();
        Assert.True(keywords.Count >= 4); // 3 AI picks + suggestion
        // The fake suggestion (5400 vol / 22 diff) out-ranks the flat AI metrics.
        Assert.Equal("deployment automation tool tutorial", keywords[0].GetProperty("term").GetString());
        Assert.Equal("dataforseo-suggestion", keywords[0].GetProperty("source").GetString());

        // Plan persisted as an artifact.
        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        Assert.Contains(previews!, p => p.Kind == "seo-keyword-plan");
        Assert.Contains(previews!, p => p.Kind == "seo-brief");
    }

    [Fact]
    public async Task Seo_analyze_persists_report_and_share_publishes_encoded_html()
    {
        var publicStore = new FakePublicStore();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new FakeSeo()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(publicStore));
        }));
        var client = await AuthedClientAsync(app);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("SEO campaign", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;

        // Keyword contains markup — the snapshot must encode it.
        var analyze = await client.PostAsJsonAsync("/api/v1/seo/analyze",
            new { campaignId = campaign.Id, keyword = "<script>alert(1)</script>" });
        Assert.Equal(HttpStatusCode.Created, analyze.StatusCode);
        using var created = JsonDocument.Parse(await analyze.Content.ReadAsStringAsync());
        var reportId = created.RootElement.GetProperty("reportArtifactId").GetGuid();

        var share = await client.PostAsync($"/api/v1/seo/reports/{reportId}/share", null);
        Assert.Equal(HttpStatusCode.OK, share.StatusCode);
        Assert.Contains("public.example", await share.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.NotNull(publicStore.LastHtml);
        Assert.DoesNotContain("<script>", publicStore.LastHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", publicStore.LastHtml, StringComparison.Ordinal);
        Assert.Contains("72", publicStore.LastHtml, StringComparison.Ordinal);
    }
}
