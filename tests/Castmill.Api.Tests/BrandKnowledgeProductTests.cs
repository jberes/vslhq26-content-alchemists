using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Knowledge;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// One gateway, several products (ADR-061). The Infragistics gateway routes to a per-product
/// agent on a body field, so brands that share an endpoint differ only by that value — and
/// reusing another brand's settings has to happen server-side, because the token is write-only.
/// </summary>
[Collection("api")]
public sealed class BrandKnowledgeProductTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task A_source_carries_its_product_and_reports_it_without_the_token()
    {
        var client = await AuthedAsync();
        var brand = await CreateBrandAsync(client, "Reveal");

        var created = await client.PostAsJsonAsync($"/api/v1/brands/{brand}/knowledge/sources",
            new BrandKnowledgeSourceRequest("Infragistics gateway", "https://ai-agent-gateway.example.com",
                "/api/agents/invokeByProductType", "input", Token: "mcp-secret-token", ProductType: "reveal"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("mcp-secret-token", body, StringComparison.Ordinal);

        var source = (await created.Content.ReadFromJsonAsync<BrandKnowledgeSourceResponse>())!;
        Assert.Equal("reveal", source.ProductType);
        Assert.Equal("productType", source.ProductField);
        Assert.True(source.HasToken);
    }

    [Fact]
    public async Task Another_brand_reuses_the_gateway_with_only_the_product_changed()
    {
        var client = await AuthedAsync();
        var reveal = await CreateBrandAsync(client, "Reveal");
        var igniteUi = await CreateBrandAsync(client, "Ignite UI");

        var origin = (await (await client.PostAsJsonAsync($"/api/v1/brands/{reveal}/knowledge/sources",
            new BrandKnowledgeSourceRequest("Infragistics gateway", "https://ai-agent-gateway.example.com",
                "/api/agents/invokeByProductType", "input", Token: "mcp-secret-token", ProductType: "reveal")))
            .Content.ReadFromJsonAsync<BrandKnowledgeSourceResponse>())!;

        var copied = await client.PostAsJsonAsync($"/api/v1/brands/{igniteUi}/knowledge/sources/copy",
            new BrandKnowledgeSourceCopyRequest(reveal, origin.Id, ProductType: "igniteui"));
        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);

        var copy = (await copied.Content.ReadFromJsonAsync<BrandKnowledgeSourceResponse>())!;
        Assert.Equal(igniteUi, copy.BrandId);
        Assert.Equal("igniteui", copy.ProductType);
        // Everything else came across, including the token the client never saw.
        Assert.Equal(origin.BaseUrl, copy.BaseUrl);
        Assert.Equal(origin.QueryPath, copy.QueryPath);
        Assert.Equal(origin.QueryField, copy.QueryField);
        Assert.True(copy.HasToken);
        Assert.DoesNotContain("mcp-secret-token", await copied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_brand_in_another_workspace_cannot_be_used_as_a_source()
    {
        var owner = await AuthedAsync();
        var reveal = await CreateBrandAsync(owner, "Reveal");
        var origin = (await (await owner.PostAsJsonAsync($"/api/v1/brands/{reveal}/knowledge/sources",
            new BrandKnowledgeSourceRequest("Gateway", "https://ai-agent-gateway.example.com",
                "/api/agents/invokeByProductType", "input", Token: "mcp-secret-token", ProductType: "reveal")))
            .Content.ReadFromJsonAsync<BrandKnowledgeSourceResponse>())!;

        // A different workspace: its own brand, and no grant on the first.
        var stranger = await AuthedAsync();
        var theirs = await CreateBrandAsync(stranger, "Someone else");

        var refused = await stranger.PostAsJsonAsync($"/api/v1/brands/{theirs}/knowledge/sources/copy",
            new BrandKnowledgeSourceCopyRequest(reveal, origin.Id, ProductType: "igniteui"));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    private async Task<HttpClient> AuthedAsync()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"kb-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Brand Owner"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private static async Task<Guid> CreateBrandAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest(name, null));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!.Id;
    }
}

/// <summary>The product travels as its own field, never folded into the question text.</summary>
public sealed class KnowledgeProductBodyTests
{
    [Fact]
    public async Task The_product_is_posted_beside_the_question()
    {
        string? captured = null;
        var handler = new CapturingHandler(request =>
        {
            captured = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return """{"output":"Reveal embeds dashboards.","citations":[]}""";
        });
        var client = new KnowledgeBaseClient(
            new SingleClientFactory(handler),
            new NoSecrets(),
            Microsoft.Extensions.Options.Options.Create(new KnowledgeBaseOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeBaseClient>.Instance);

        var endpoint = new KnowledgeEndpoint(
            "https://ai-agent-gateway.example.com", "/api/agents/invokeByProductType", "input",
            "token", "Gateway", ProductType: "reveal");

        var answer = await client.AskAsync(Guid.NewGuid(), "How do I create a dashboard?", endpoint, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Contains("\"input\":\"How do I create a dashboard?\"", captured, StringComparison.Ordinal);
        Assert.Contains("\"productType\":\"reveal\"", captured, StringComparison.Ordinal);
        Assert.EndsWith("/api/agents/invokeByProductType", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gateway_without_a_product_posts_only_the_question()
    {
        string? captured = null;
        var handler = new CapturingHandler(request =>
        {
            captured = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return """{"output":"Answer.","citations":[]}""";
        });
        var client = new KnowledgeBaseClient(
            new SingleClientFactory(handler), new NoSecrets(),
            Microsoft.Extensions.Options.Options.Create(new KnowledgeBaseOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeBaseClient>.Instance);

        await client.AskAsync(Guid.NewGuid(),
            "What is this?",
            new KnowledgeEndpoint("https://rag.example.com", "/query", "query", "token", "RAG"),
            CancellationToken.None);

        Assert.Equal("{\"query\":\"What is this?\"}", captured);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public string LastPath { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoSecrets : Castmill.Api.Services.Secrets.IUserSecretsService
    {
        public Task SetAsync(Guid userId, Castmill.Api.Services.Secrets.SecretKind kind, string value, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<string?> GetAsync(Guid userId, Castmill.Api.Services.Secrets.SecretKind kind, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<bool> RemoveAsync(Guid userId, Castmill.Api.Services.Secrets.SecretKind kind, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<IReadOnlyDictionary<Castmill.Api.Services.Secrets.SecretKind, DateTimeOffset>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Castmill.Api.Services.Secrets.SecretKind, DateTimeOffset>>(
                new Dictionary<Castmill.Api.Services.Secrets.SecretKind, DateTimeOffset>());
    }
}
