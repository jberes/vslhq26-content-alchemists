using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

/// <summary>The Brand editor's Knowledge tab (ADR-056): RAG endpoint, skills, MCP servers.</summary>
public sealed class BrandKnowledgeUiTests : CastmillUiTestContext
{
    private static readonly Guid BrandId = Guid.Parse("d5555555-1111-1111-1111-111111111111");

    public BrandKnowledgeUiTests()
    {
        SignInTestUser();
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Ignite UI", null, null, DateTimeOffset.UtcNow));
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
}
