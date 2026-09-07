using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

/// <summary>The Brand editor's Knowledge tab (ADR-056): RAG endpoint, skills, MCP servers.</summary>
public sealed class BrandKnowledgeUiTests : CastmillUiTestContext
{
    private static readonly Guid BrandId = Guid.Parse("d5555555-1111-1111-1111-111111111111");
    private static readonly Guid OtherBrandId = Guid.Parse("d5555555-1111-1111-1111-222222222222");
    private static readonly Guid OtherSourceId = Guid.Parse("d5555555-1111-1111-1111-333333333333");

    public BrandKnowledgeUiTests()
    {
        SignInTestUser();
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Ignite UI", null, null, DateTimeOffset.UtcNow));
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>
        {
            new(BrandId, "Ignite UI", null, null, DateTimeOffset.UtcNow),
            new(OtherBrandId, "Reveal", null, null, DateTimeOffset.UtcNow),
        });
        Http.OnGet($"api/v1/brands/{BrandId}/assets", new List<BrandAssetResponse>());
        Http.OnGet($"api/v1/brands/{BrandId}/templates", new List<BrandTemplateResponse>());
        Http.OnGet($"api/v1/brands/{BrandId}/knowledge", new BrandKnowledgeResponse(
            [new BrandKnowledgeSourceResponse(Guid.NewGuid(), BrandId, "Infragistics RAG", "https://rag.infragistics.example", "/query", "query", true, true, DateTimeOffset.UtcNow)],
            [new BrandSkillResponse(Guid.NewGuid(), BrandId, "ignite-react", "SKILL.md", "# Ignite UI for React\nUse IgrGrid…", "blog, youtube", true, DateTimeOffset.UtcNow)],
            [new BrandMcpServerResponse(Guid.NewGuid(), BrandId, "ig-docs", "https://mcp.infragistics.example/sse", true, ["search_docs"], true, DateTimeOffset.UtcNow)]));
    }

    [Fact]
    public async Task The_knowledge_tab_lists_sources_skills_and_servers_without_exposing_secrets()
    {
        var view = Render<BrandEditor>(p => p.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(() => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));

        await view.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Knowledge").ClickAsync();
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-knowledge")));

        var text = view.Find(".cm-knowledge").TextContent;
        Assert.Contains("Infragistics RAG", text, StringComparison.Ordinal);
        Assert.Contains("token stored", text, StringComparison.Ordinal);
        Assert.Contains("ignite-react", text, StringComparison.Ordinal);
        Assert.Contains("blog, youtube", text, StringComparison.Ordinal);
        Assert.Contains("ig-docs", text, StringComparison.Ordinal);
        Assert.Contains("1 tools", text, StringComparison.Ordinal);
        // Secrets are write-only: the response carries flags, never values, and the page has nothing to show.
        Assert.DoesNotContain("Bearer ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_knowledge_base_posts_the_endpoint_and_token()
    {
        var view = Render<BrandEditor>(p => p.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(() => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Knowledge").ClickAsync();
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-knowledge")));

        // Re-find before each change: every bound input re-renders the form (bUnit's stale-handler rule).
        view.Find("input[aria-label='Knowledge source name']").Change("Support RAG");
        view.Find("input[aria-label='Base URL']").Change("https://rag.example.com");
        view.Find("input[aria-label='Bearer token']").Change("secret-token");

        Http.OnPost($"api/v1/brands/{BrandId}/knowledge/sources",
            new BrandKnowledgeSourceResponse(Guid.NewGuid(), BrandId, "Support RAG", "https://rag.example.com", "/query", "query", true, true, DateTimeOffset.UtcNow));
        await view.FindAll(".cm-knowledge__form button").Single(b => b.TextContent.Contains("Add knowledge base", StringComparison.Ordinal)).ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Method == HttpMethod.Post && b.Path.EndsWith("/knowledge/sources", StringComparison.Ordinal)).Body;
            Assert.Contains("https://rag.example.com", body, StringComparison.Ordinal);
            Assert.Contains("\"token\":\"secret-token\"", body, StringComparison.Ordinal);
        });
        Assert.Contains("Knowledge base saved.", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reusing_another_brands_gateway_copies_it_server_side_with_this_brands_product()
    {
        Http.OnGet($"api/v1/brands/{OtherBrandId}/knowledge", new BrandKnowledgeResponse(
            [new BrandKnowledgeSourceResponse(OtherSourceId, OtherBrandId, "Infragistics gateway",
                "https://ai-agent-gateway.example.com", "/api/agents/invokeByProductType", "input",
                true, true, DateTimeOffset.UtcNow, ProductType: "reveal")],
            [], []));
        Http.OnPost($"api/v1/brands/{BrandId}/knowledge/sources/copy",
            new BrandKnowledgeSourceResponse(Guid.NewGuid(), BrandId, "Infragistics gateway",
                "https://ai-agent-gateway.example.com", "/api/agents/invokeByProductType", "input",
                true, true, DateTimeOffset.UtcNow, ProductType: "igniteui"));

        var view = Render<BrandEditor>(p => p.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(() => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Knowledge").ClickAsync();
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-knowledge__reuse")));

        // Only the other brand is offered — a brand cannot copy from itself.
        var brands = view.Find("select[aria-label='Brand to copy from']");
        Assert.DoesNotContain("Ignite UI", brands.TextContent, StringComparison.Ordinal);
        await brands.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = OtherBrandId.ToString() });

        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find("select[aria-label='Knowledge base to copy']")));
        view.Find("select[aria-label='Knowledge base to copy']").Change(OtherSourceId.ToString());
        view.Find("input[aria-label='Product for the copy']").Change("igniteui");
        await view.FindAll(".cm-knowledge__reuse button").Single(b => b.TextContent.Contains("Use these settings", StringComparison.Ordinal)).ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var body = Http.Bodies.Single(b => b.Path.EndsWith("knowledge/sources/copy", StringComparison.Ordinal)).Body;
            Assert.Contains($"\"sourceBrandId\":\"{OtherBrandId}\"", body, StringComparison.Ordinal);
            Assert.Contains($"\"sourceId\":\"{OtherSourceId}\"", body, StringComparison.Ordinal);
            Assert.Contains("\"productType\":\"igniteui\"", body, StringComparison.Ordinal);
            // The token is never in the request: the server copies it.
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task A_sources_product_is_shown_on_its_row()
    {
        Http.OnGet($"api/v1/brands/{BrandId}/knowledge", new BrandKnowledgeResponse(
            [new BrandKnowledgeSourceResponse(Guid.NewGuid(), BrandId, "Infragistics gateway",
                "https://ai-agent-gateway.example.com", "/api/agents/invokeByProductType", "input",
                true, true, DateTimeOffset.UtcNow, ProductType: "reveal")],
            [], []));

        var view = Render<BrandEditor>(p => p.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(() => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Knowledge").ClickAsync();
        await view.WaitForAssertionAsync(() =>
            Assert.Contains("productType: reveal", view.Find(".cm-knowledge").TextContent, StringComparison.Ordinal));
    }
}
