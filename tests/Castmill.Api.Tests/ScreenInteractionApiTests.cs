using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ScreenInteractionApiTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Settings_and_secrets_can_be_removed_without_affecting_another_user()
    {
        using var owner = await SignInAsync();
        using var stranger = await SignInAsync();
        using var anonymous = factory.CreateClient();
        const string setting = "/api/v1/settings/images.default-model";
        const string secret = "/api/v1/settings/secrets/NanoBananaKey";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(setting)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(secret)).StatusCode);

        (await owner.PutAsJsonAsync(setting, new { value = "image-alt" })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync(secret, new { value = "test-only-not-a-provider-key" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync(setting)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync(secret)).StatusCode);
        Assert.Contains("image-alt", await owner.GetStringAsync("/api/v1/settings"), StringComparison.Ordinal);
        var before = await owner.GetFromJsonAsync<List<SecretState>>("/api/v1/settings/secrets");
        Assert.True(before!.Single(row => row.Kind == "NanoBananaKey").Configured);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(setting)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(secret)).StatusCode);
        Assert.DoesNotContain("images.default-model", await owner.GetStringAsync("/api/v1/settings"), StringComparison.Ordinal);
        var after = await owner.GetFromJsonAsync<List<SecretState>>("/api/v1/settings/secrets");
        Assert.False(after!.Single(row => row.Kind == "NanoBananaKey").Configured);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(setting)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(secret)).StatusCode);
    }

    [Fact]
    public async Task Brand_templates_skills_and_mcp_servers_round_trip_and_delete()
    {
        using var owner = await SignInAsync();
        using var stranger = await SignInAsync();
        var brand = await CreateAsync<BrandProfileDetailResponse>(owner, "/api/v1/brands", new { name = "Interaction brand" });
        var root = $"/api/v1/brands/{brand.Id}";
        try
        {
            var template = await CreateAsync<BrandTemplateResponse>(owner, $"{root}/templates",
                new BrandTemplateRequest("blog", "Draft", "Write from evidence."));
            var templateUrl = $"{root}/templates/{template.Id}";
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync(templateUrl)).StatusCode);
            (await owner.PutAsJsonAsync(templateUrl,
                new BrandTemplateRequest("blog", "Updated", "Keep citations.", true))).EnsureSuccessStatusCode();
            var templates = await owner.GetFromJsonAsync<List<BrandTemplateResponse>>($"{root}/templates");
            Assert.Equal("Keep citations.", templates!.Single(row => row.Id == template.Id).SteeringPrompt);
            (await owner.DeleteAsync(templateUrl)).EnsureSuccessStatusCode();
            Assert.DoesNotContain((await owner.GetFromJsonAsync<List<BrandTemplateResponse>>($"{root}/templates"))!,
                row => row.Id == template.Id);

            var skill = await CreateAsync<BrandSkillResponse>(owner, $"{root}/knowledge/skills",
                new BrandSkillRequest("Grid", "grid.md", "Verify accessibility."));
            var skillUrl = $"{root}/knowledge/skills/{skill.Id}";
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync(skillUrl,
                new BrandSkillRequest("Intruder", "grid.md", "Overwritten"))).StatusCode);
            (await owner.PutAsJsonAsync(skillUrl,
                new BrandSkillRequest("Grid revised", "grid.md", "Cite accessibility docs.", "blog"))).EnsureSuccessStatusCode();

            var server = await CreateAsync<BrandMcpServerResponse>(owner, $"{root}/knowledge/mcp-servers",
                new BrandMcpServerRequest("docs", "https://example.test/mcp", "Bearer test-only"));
            var serverUrl = $"{root}/knowledge/mcp-servers/{server.Id}";
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync(serverUrl)).StatusCode);
            (await owner.PutAsJsonAsync(serverUrl,
                new BrandMcpServerRequest("docs-updated", "https://example.test/mcp", AllowedTools: ["search"])))
                .EnsureSuccessStatusCode();
            var knowledge = await owner.GetFromJsonAsync<BrandKnowledgeResponse>($"{root}/knowledge");
            Assert.Equal("Cite accessibility docs.", knowledge!.Skills.Single(row => row.Id == skill.Id).Content);
            Assert.True(knowledge.McpServers.Single(row => row.Id == server.Id).HasAuthorization);
            Assert.Equal(["search"], knowledge.McpServers.Single(row => row.Id == server.Id).AllowedTools);
            Assert.DoesNotContain("Bearer test-only", await owner.GetStringAsync($"{root}/knowledge"), StringComparison.Ordinal);

            (await owner.DeleteAsync(skillUrl)).EnsureSuccessStatusCode();
            (await owner.DeleteAsync(serverUrl)).EnsureSuccessStatusCode();
            knowledge = await owner.GetFromJsonAsync<BrandKnowledgeResponse>($"{root}/knowledge");
            Assert.Empty(knowledge!.Skills);
            Assert.Empty(knowledge.McpServers);

            var asset = await CreateAsync<AssetResponse>(owner, "/api/v1/assets", new AssetCreateRequest("kit.png", "image/png", 128));
            var linked = await CreateAsync<BrandAssetResponse>(owner, $"{root}/assets", new BrandAssetLinkRequest(asset.Id, "product", "Product"));
            (await owner.DeleteAsync($"{root}/assets/{linked.Id}")).EnsureSuccessStatusCode();
            Assert.Empty((await owner.GetFromJsonAsync<List<BrandAssetResponse>>($"{root}/assets"))!);
            (await owner.DeleteAsync($"/api/v1/assets/{asset.Id}")).EnsureSuccessStatusCode();
        }
        finally
        {
            (await owner.DeleteAsync(root)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Focus_exports_persisted_markdown_and_docx_and_rejects_strangers()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Cors:AllowedOrigins:0", "https://browser.example.test"));
        using var owner = await SignInAsync(app);
        owner.DefaultRequestHeaders.Add("Origin", "https://browser.example.test");
        using var stranger = await SignInAsync();
        using var anonymous = factory.CreateClient();
        var campaign = await CreateAsync<CampaignResponse>(owner, "/api/v1/campaigns", new CampaignCreateRequest("Export flow", null));
        var root = $"/api/v1/campaigns/{campaign.Id}";
        try
        {
            var artifact = await CreateAsync<ArtifactResponse>(owner, $"{root}/artifacts",
                new ArtifactCreateRequest("blog", "Exported story", """{"content":{"markdown":"# Exported story\n\nSaved evidence."}}"""));
            var export = $"{root}/artifacts/{artifact.Id}/export";
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(export)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(export)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"{export}?format=exe")).StatusCode);
            var markdown = await owner.GetAsync($"{export}?format=md");
            markdown.EnsureSuccessStatusCode();
            Assert.Contains("Content-Disposition", string.Join(",", markdown.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("text/markdown", markdown.Content.Headers.ContentType!.MediaType);
            Assert.Contains("Saved evidence.", await markdown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var word = await owner.GetAsync($"{export}?format=docx");
            word.EnsureSuccessStatusCode();
            using var archive = new ZipArchive(new MemoryStream(await word.Content.ReadAsByteArrayAsync()));
            Assert.NotNull(archive.GetEntry("word/document.xml"));
        }
        finally
        {
            (await owner.DeleteAsync(root)).EnsureSuccessStatusCode();
        }
    }

    private async Task<HttpClient> SignInAsync(WebApplicationFactory<Program>? app = null)
    {
        var client = (app ?? factory).CreateClient();
        var auth = await CreateAsync<AuthResponse>(client, "/api/v1/auth/register",
            new RegisterRequest($"screen-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Screen Tester"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<TResponse> CreateAsync<TResponse>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private sealed record SecretState(string Kind, bool Configured);
}