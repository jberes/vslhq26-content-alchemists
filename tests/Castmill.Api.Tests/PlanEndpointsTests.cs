using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Images;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// The endpoints ADR-055/056/057 added: overlay spec, region edit, technical brief and claims,
/// media links, Brand knowledge — against a real SQL Server (Testcontainers).
/// </summary>
[Collection("api")]
public sealed class PlanEndpointsTests(CastmillApiFactory factory)
{
    // ---- Overlay + region edit (ADR-055) ----------------------------------------------

    [Fact]
    public async Task An_overlay_spec_is_stored_and_composited_onto_a_placed_take()
    {
        await using var app = WithImageFakes();
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);

        // Generate + place a take so the overlay has something to land on.
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var take = Assert.Single(batch.Variants);
        Assert.NotNull(take.Prompt);
        (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/place",
            new { variantId = take.Id })).EnsureSuccessStatusCode();

        var spec = new OverlaySpec([new OverlayBox("h", "Deploy time, halved", 0.08, 0.72, 0.84, 0.18, 0.11, 700, "#F2F2F3", "left", new OverlayBand("#000000", 0.9))]);
        var put = await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/overlay", spec);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var slot = (await client.GetFromJsonAsync<List<ImageSlotResponse>>($"/api/v1/campaigns/{campaignId}/image-slots"))!
            .Single(s => s.Id == slotId);
        Assert.NotNull(slot.Overlay);
        Assert.Equal("Deploy time, halved", slot.Overlay!.Boxes[0].Text);
        Assert.Contains("/composited/", slot.PublishedUrl, StringComparison.Ordinal);

        var cleared = await client.DeleteAsync($"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/overlay");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var after = (await cleared.Content.ReadFromJsonAsync<ImageSlotResponse>())!;
        Assert.Null(after.Overlay);
        Assert.Equal(after.BaseImageUrl, after.PublishedUrl);
    }

    [Fact]
    public async Task A_region_edit_needs_a_painted_mask_and_then_lands_as_a_child_take()
    {
        await using var app = WithImageFakes();
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var source = Assert.Single(batch.Variants);

        var empty = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{source.Id}/edit",
            new { maskPng = Convert.ToBase64String(Png(64, 64, SKColors.Black)), instruction = "replace the background" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("Paint the region", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var edited = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{source.Id}/edit",
            new { maskPng = "data:image/png;base64," + Convert.ToBase64String(Png(64, 64, SKColors.White)), instruction = "replace the background" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var result = (await edited.Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var child = Assert.Single(result.Variants);
        Assert.Equal(source.Id, child.SourceVariantId);
        Assert.Equal("edit: replace the background", child.SteeringNote);
    }

    // ---- Technical brief + claims (ADR-056) ---------------------------------------------

    [Fact]
    public async Task The_technical_brief_is_metadata_and_reaches_the_tech_edit_with_its_claims()
    {
        var prompts = new ConcurrentBag<string>();
        await using var app = WithFakeModel(prompt =>
        {
            prompts.Add(prompt);
            return prompt.Contains("You are the technical editor", StringComparison.Ordinal)
                ? $$"""
                    {"artifact":{"title":"Launch story","markdown":"{{Body}} cut deployment time by 47%.",
                     "metaDescription":"d","citations":["S1","S2"]},
                     "changes":[{"what":"Added the figure","why":"docs","sourceUrl":"https://docs.example/x"}],
                     "claims":[{"statement":"IgrGrid virtualises rows","sourceUrl":"https://docs.example/grid"},
                               {"statement":"Ships a Vue adapter","sourceUrl":null}]}
                    """
                : Draft(prompt);
        });
        var (client, campaignId, artifactId) = await SetUpArtifactAsync(app);

        var before = (await client.GetFromJsonAsync<ArtifactResponse>($"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}"))!;
        var put = await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}/technical-brief",
            new TechnicalBrief("Ignite UI for React", "24.2", "IgrGrid", null, null, "Vue support"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var stored = (await put.Content.ReadFromJsonAsync<ArtifactResponse>())!;
        Assert.Equal("Ignite UI for React", stored.TechnicalBrief!.Product);
        Assert.Equal(before.Version, stored.Version); // metadata: no version bump

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/artifacts/{artifactId}/tech-edit",
            new { steering = "Be precise.", useKnowledgeBase = false });
        var result = (await response.Content.ReadFromJsonAsync<TechEditResult>())!;
        Assert.True(result.Success, result.Error);

        var techEditPrompt = prompts.Single(p => p.Contains("You are the technical editor", StringComparison.Ordinal));
        Assert.Contains("TECHNICAL BRIEF", techEditPrompt, StringComparison.Ordinal);
        Assert.Contains("Must NOT claim: Vue support", techEditPrompt, StringComparison.Ordinal);

        Assert.NotNull(result.Claims);
        Assert.Equal(2, result.Claims!.Count);
        Assert.Contains(result.Claims, c => c.Verified && c.SourceUrl == "https://docs.example/grid");
        Assert.Contains(result.Warnings, w => w.StartsWith("Unverified claim: Ships a Vue adapter", StringComparison.Ordinal));

        // Clearing: an empty brief removes it.
        var clear = await client.PutAsJsonAsync($"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}/technical-brief", new TechnicalBrief());
        Assert.Null((await clear.Content.ReadFromJsonAsync<ArtifactResponse>())!.TechnicalBrief);
    }

    // ---- Media links (ADR-057) ------------------------------------------------------------

    [Fact]
    public async Task A_local_recording_is_linked_on_ingest_and_can_be_re_linked_or_cleared()
    {
        await using var app = WithFakeModel(Draft);
        var client = await AuthedClientAsync(app, "media");
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Media", null)))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts", new
        {
            text = "Welcome to the webinar. Today we ship the grid. It virtualises rows. Questions at the end.",
            source = "webinar.mp4",
            segments = new[] { new { id = "S1", startSeconds = 0.0, endSeconds = 4.0, text = "Welcome to the webinar. Today we ship the grid." },
                               new { id = "S2", startSeconds = 4.0, endSeconds = 8.0, text = "It virtualises rows. Questions at the end." } },
            localPath = "/Users/jason/Movies/webinar.mp4",
            contentHash = "sz1000-abc",
        });
        ingest.EnsureSuccessStatusCode();

        var sources = (await client.GetFromJsonAsync<List<SourceAssetResponse>>($"/api/v1/campaigns/{campaign.Id}/sources"))!;
        var source = Assert.Single(sources);
        Assert.Equal("/Users/jason/Movies/webinar.mp4", source.LocalPath);
        Assert.Equal("sz1000-abc", source.ContentHash);
        Assert.Null(source.MediaAssetId);

        var moved = await client.PatchAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/sources/{source.Id}/media",
            new SourceMediaLinkRequest("/Volumes/Archive/webinar.mp4", "sz1000-abc"));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("/Volumes/Archive/webinar.mp4", (await moved.Content.ReadFromJsonAsync<SourceAssetResponse>())!.LocalPath);

        var unknownAsset = await client.PatchAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/sources/{source.Id}/media",
            new SourceMediaLinkRequest(MediaAssetId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, unknownAsset.StatusCode);

        var cleared = await client.PatchAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/sources/{source.Id}/media",
            new SourceMediaLinkRequest(LocalPath: "", ContentHash: ""));
        var afterClear = (await cleared.Content.ReadFromJsonAsync<SourceAssetResponse>())!;
        Assert.Null(afterClear.LocalPath);
        Assert.Null(afterClear.ContentHash);
    }

    // ---- Brand knowledge (ADR-056) --------------------------------------------------------

    [Fact]
    public async Task Brand_knowledge_is_owner_scoped_write_only_for_secrets_and_flags_the_campaign()
    {
        await using var app = WithFakeModel(Draft);
        var client = await AuthedClientAsync(app, "know");
        var brand = (await (await client.PostAsJsonAsync("/api/v1/brands", new BrandProfileUpsertRequest("Ignite UI", null)))
            .Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;

        var created = await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/knowledge/sources",
            new BrandKnowledgeSourceRequest("Support RAG", "https://rag.example.com", "/ask", "question", "secret-token"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = (await created.Content.ReadFromJsonAsync<BrandKnowledgeSourceResponse>())!;
        Assert.True(source.HasToken);
        Assert.DoesNotContain("secret-token", await created.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var skill = await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/knowledge/skills",
            new BrandSkillRequest("ignite-react", "SKILL.md", "# Ignite UI for React\nAlways name IgrGrid.", "blog, youtube"));
        Assert.Equal(HttpStatusCode.Created, skill.StatusCode);

        var plainHttp = await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/knowledge/mcp-servers",
            new BrandMcpServerRequest("ig-docs", "http://mcp.example.com/sse", "Bearer x"));
        Assert.Equal(HttpStatusCode.BadRequest, plainHttp.StatusCode);
        var mcp = await client.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/knowledge/mcp-servers",
            new BrandMcpServerRequest("ig-docs", "https://mcp.example.com/sse", "Bearer x", ["search_docs"]));
        Assert.Equal(HttpStatusCode.Created, mcp.StatusCode);
        Assert.DoesNotContain("Bearer x", await mcp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var knowledge = (await client.GetFromJsonAsync<BrandKnowledgeResponse>($"/api/v1/brands/{brand.Id}/knowledge"))!;
        Assert.Single(knowledge.Sources);
        Assert.Single(knowledge.Skills);
        Assert.Equal(["search_docs"], knowledge.McpServers.Single().AllowedTools);

        // Updating without a token keeps the stored one; an empty token clears it.
        var kept = await client.PutAsJsonAsync($"/api/v1/brands/{brand.Id}/knowledge/sources/{source.Id}",
            new BrandKnowledgeSourceRequest("Support RAG v2", "https://rag.example.com"));
        Assert.True((await kept.Content.ReadFromJsonAsync<BrandKnowledgeSourceResponse>())!.HasToken);
        var clearedToken = await client.PutAsJsonAsync($"/api/v1/brands/{brand.Id}/knowledge/sources/{source.Id}",
            new BrandKnowledgeSourceRequest("Support RAG v2", "https://rag.example.com", Token: ""));
        Assert.False((await clearedToken.Content.ReadFromJsonAsync<BrandKnowledgeSourceResponse>())!.HasToken);

        // A campaign on this brand advertises the knowledge so Focus can offer it.
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("K", null, brand.Id)))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;
        var preview = await client.GetStringAsync($"/api/v1/campaigns/{campaign.Id}/preview");
        Assert.Contains("\"hasKnowledge\":true", preview, StringComparison.Ordinal);

        // Another tenant sees nothing.
        var stranger = await AuthedClientAsync(app, "stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/brands/{brand.Id}/knowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/v1/brands/{brand.Id}/knowledge/sources/{source.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/brands/{brand.Id}/knowledge/sources/{source.Id}")).StatusCode);
    }

    // ---- helpers ------------------------------------------------------------------------

    private WebApplicationFactory<Program> WithFakeModel(Func<string, string> respond) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => new FakeFactory(respond)))));

    private WebApplicationFactory<Program> WithImageFakes() =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => new SolidRenderer()));
            s.Replace(ServiceDescriptor.Scoped<IImageProviderRegistry>(_ => new ReadyRegistry()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(new MemoryPublicStore()));
        }));

    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> app, string tag)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"{tag}-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Plan Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<(HttpClient Client, Guid CampaignId, Guid SlotId)> SetUpSlotAsync(WebApplicationFactory<Program> app)
    {
        var client = await AuthedClientAsync(app, "slot");
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Takes", null)))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;
        var slots = (await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>())!;
        var slot = slots.Single(s => s.Kind == "youtube-thumbnail");
        (await client.PatchAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}",
            JsonContent.Create(new { prompt = "a bold thumbnail", promptMode = "Manual" }))).EnsureSuccessStatusCode();
        return (client, campaign.Id, slot.Id);
    }

    private static async Task<(HttpClient Client, Guid CampaignId, Guid ArtifactId)> SetUpArtifactAsync(WebApplicationFactory<Program> app)
    {
        var client = await AuthedClientAsync(app, "brief");
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Brief", null)))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts", new
        {
            text = "We launched the new product. It cut deployment time in half. Customers love the new dashboard. The team shipped it in six weeks.",
            source = "unit-test",
        });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!.TranscriptArtifactId;
        var generate = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/generate/blog", new { transcriptArtifactId = transcriptId });
        var blog = (await generate.Content.ReadFromJsonAsync<GenerationResult>())!;
        Assert.True(blog.Success, blog.Error);
        return (client, campaign.Id, blog.ArtifactId!.Value);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);

    private const string Words = "word word word word word";
    private static string Body => string.Join(" ", Enumerable.Repeat(Words, 360));

    private static string Draft(string prompt)
    {
        if (prompt.Contains("Create an outline", StringComparison.Ordinal))
        {
            return """{"title":"Launch story","sections":[{"heading":"Intro","segmentIds":["S1"]}],"citations":["S1"]}""";
        }
        if (prompt.Contains("auditing a blog draft", StringComparison.Ordinal))
        {
            return """{"unsupportedClaims":[],"citations":["S1"]}""";
        }
        return $$"""{"title":"Launch story","markdown":"{{Body}}","metaDescription":"d","citations":["S1","S2"]}""";
    }

    private static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private sealed class FakeFactory(Func<string, string> respond) : IFoundryClientFactory
    {
        public Task<FoundryCredentials?> ResolveCredentialsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<FoundryCredentials?>(new FoundryCredentials("https://fake.local", "fake", "config"));
        public string? ResolveDeployment(string modelAlias) => "fake-deployment";
        public Task<FoundryTarget?> ResolveTargetAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<FoundryTarget?>(new FoundryTarget(new FoundryCredentials("https://fake.local", "fake", "config"), "fake-deployment"));
        public Task<IChatClient> CreateChatClientAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult<IChatClient>(new FakeChatClient(respond));
    }

    private sealed class FakeChatClient(Func<string, string> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, respond(string.Join("\n", messages.Select(m => m.Text))))));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SolidRenderer : IImageRenderer
    {
        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct) =>
            Task.FromResult(Png(64, 64, SKColors.Teal));
        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct) =>
            Task.FromResult(Png(width, height, SKColors.Teal));
        public Task<byte[]> RenderEditAsync(Guid userId, string instruction, byte[] image, byte[] maskPng, int width, int height, string? modelAlias, CancellationToken ct) =>
            Task.FromResult(Png(width, height, SKColors.Orange));
    }

    private sealed class ReadyRegistry : IImageProviderRegistry
    {
        private readonly ReadyProvider _provider = new();
        public IImageProvider Resolve(string? modelAliasOrProvider) => _provider;
        public Task<IReadOnlyList<ImageProviderStatus>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImageProviderStatus>>([new ImageProviderStatus("test", true, null, SupportsReferenceImages: true)]);
    }

    private sealed class ReadyProvider : IImageProvider
    {
        public string Name => "test";
        public Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult(new ImageProviderStatus(Name, true, null, SupportsReferenceImages: true));
        public Task<byte[]> GenerateAsync(Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct) =>
            throw new NotSupportedException("The renderer test double owns generation.");
    }

    private sealed class MemoryPublicStore : IPublicContentStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
        public bool IsConfigured => true;
        public Task DeleteAsync(string path, CancellationToken ct) { _blobs.TryRemove(path, out _); return Task.CompletedTask; }
        public Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct)
        { _blobs[path] = bytes.ToArray(); return Task.FromResult(new Uri($"https://public.example/{path}")); }
        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
            Task.FromResult(_blobs.TryGetValue(path, out var bytes) ? bytes : null);
    }
}
