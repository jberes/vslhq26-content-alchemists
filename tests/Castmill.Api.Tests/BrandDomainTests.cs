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
/// The brand kit (item 5/6 of the UX overhaul): typed style card, templates, campaign
/// association + context links, brand-delete detachment, and — the point of it all —
/// brand steering actually reaching the prompt.
/// </summary>
[Collection("api")]
public sealed class BrandDomainTests(CastmillApiFactory factory)
{
    private async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program>? app = null)
    {
        var client = (app ?? factory).CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"brand-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Brand Tester"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<BrandProfileDetailResponse> CreateBrandAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/brands", new BrandProfileUpsertRequest(
            "Acme Robotics",
            new BrandStyleCard(
                Voice: "Direct, engineer-to-engineer, no hype",
                Audience: "Platform teams",
                Colors: [new BrandColor("primary", "#0A66C2")],
                ImageStyle: "Clean editorial photography, muted blues",
                BannedPhrases: ["game-changer"])));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
    }

    [Fact]
    public async Task Style_card_roundtrips_typed_and_rejects_bad_hex()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);

        Assert.Equal("Direct, engineer-to-engineer, no hype", brand.StyleCard?.Voice);
        Assert.Equal("#0A66C2", Assert.Single(brand.StyleCard!.Colors!).Hex);

        var bad = await client.PutAsJsonAsync($"/api/v1/brands/{brand.Id}", new BrandProfileUpsertRequest(
            "Acme", new BrandStyleCard(Colors: [new BrandColor("primary", "not-a-hex")])));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Templates_validate_kind_and_keep_one_default_per_kind()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);
        var baseUrl = $"/api/v1/brands/{brand.Id}/templates";

        var unknown = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("press-release", "Nope", "steer"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var first = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("newsletter", "Monthly", "Three sections, one CTA.", IsDefault: true));
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync(baseUrl,
            new BrandTemplateRequest("newsletter", "Launch special", "Louder.", IsDefault: true));
        second.EnsureSuccessStatusCode();

        var templates = await client.GetFromJsonAsync<List<BrandTemplateResponse>>(baseUrl);
        Assert.Equal(2, templates!.Count);
        Assert.Equal("Launch special", Assert.Single(templates, t => t.IsDefault).Name);
    }

    [Fact]
    public async Task Campaign_carries_brand_and_links_and_rejects_a_foreign_brand()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);

        var links = new List<CampaignLink>
        {
            new("Home page", "https://acme.example"),
            new("GitHub", "https://github.com/acme/robots", "the OSS repo"),
        };
        var create = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Branded campaign", "brief", brand.Id, links));
        create.EnsureSuccessStatusCode();
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;
        Assert.Equal(brand.Id, campaign.BrandId);
        Assert.Equal(2, campaign.Links!.Count);

        // The preview carries the brand summary for the header chip.
        var preview = await client.GetAsync($"/api/v1/campaigns/{campaign.Id}/preview");
        Assert.Contains("Acme Robotics", await preview.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Another tenant's brand id is a plain 400 — the filter hides its existence.
        var stranger = await AuthedClientAsync();
        var foreign = await stranger.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Sneaky", null, brand.Id, null));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_brand_detaches_campaigns_and_removes_the_kit()
    {
        var client = await AuthedClientAsync();
        var brand = await CreateBrandAsync(client);

        var create = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Attached", null, brand.Id, null));
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;

        (await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/templates",
            new BrandTemplateRequest("blog", "Voice", "steer", IsDefault: true))).EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"/api/v1/brands/{brand.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var reloaded = await client.GetFromJsonAsync<CampaignResponse>($"/api/v1/campaigns/{campaign.Id}");
        Assert.Null(reloaded!.BrandId);
    }

    [Fact]
    public async Task Brand_voice_template_and_context_links_reach_the_generation_prompt()
    {
        var capture = new CapturingFoundryFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));

        var client = await AuthedClientAsync(app);
        var brand = await CreateBrandAsync(client);
        (await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/templates",
            new BrandTemplateRequest("newsletter", "Monthly", "Exactly three sections and a PS.", IsDefault: true)))
            .EnsureSuccessStatusCode();

        var create = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest(
            "Steered", null, brand.Id, [new CampaignLink("Home page", "https://acme.example")]));
        var campaignId = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched. It cut deploy time in half. Everyone was pleased with the result.", source = "test" });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/generate/newsletter",
            new { transcriptArtifactId = transcriptId });
        generate.EnsureSuccessStatusCode();

        var prompt = Assert.Single(capture.Prompts, p => p.Contains("newsletter edition", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Direct, engineer-to-engineer, no hype", prompt, StringComparison.Ordinal);
        Assert.Contains("Exactly three sections and a PS.", prompt, StringComparison.Ordinal);
        Assert.Contains("https://acme.example", prompt, StringComparison.Ordinal);
        Assert.Contains("game-changer", prompt, StringComparison.Ordinal); // banned phrases listed
    }

    [Fact]
    public async Task A_campaign_without_a_brand_generates_with_no_brand_block()
    {
        var capture = new CapturingFoundryFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));

        var client = await AuthedClientAsync(app);
        var create = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Plain", null));
        var campaignId = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new { text = "We launched. It cut deploy time in half. Everyone was pleased with the result.", source = "test" });
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;

        (await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/generate/newsletter",
            new { transcriptArtifactId = transcriptId })).EnsureSuccessStatusCode();

        var prompt = Assert.Single(capture.Prompts, p => p.Contains("newsletter edition", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Brand:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Campaign context links", prompt, StringComparison.Ordinal);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);

    /// <summary>The AiGenerationTests fake, plus prompt capture for injection asserts.</summary>
    private sealed class CapturingFoundryFactory : IFoundryClientFactory
    {
        public List<string> Prompts { get; } = [];

        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(new FoundryCredentials("https://fake.local", "fake", "config"));

        public string? ResolveDeployment(string modelAlias) => "fake-deployment";

        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(
                new FoundryCredentials("https://fake.local", "fake", "config"), "fake-deployment"));

        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new CapturingChatClient(Prompts));
    }

    private sealed class CapturingChatClient(List<string> prompts) : IChatClient
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
}
