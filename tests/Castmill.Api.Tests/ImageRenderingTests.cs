using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Endpoints;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ImageRenderingTests(CastmillApiFactory factory)
{
    // ---- Unit: WebP encoding + stub replacement -------------------------------

    [Fact]
    public void Png_encodes_to_webp()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        var webp = ImageRenderer.EncodeWebp(png);
        Assert.True(webp.Length > 0);
        // RIFF....WEBP container signature
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(webp, 8, 4));
    }

    [Fact]
    public void Blog_stub_markers_are_replaced_and_version_neutral_otherwise()
    {
        const string blogJson = """{"content":{"title":"t","markdown":"Intro ![stub:blog-hero]() body ![stub:blog-inline-1]() end"},"validation":{}}""";
        var updated = ImageEndpoints.ReplaceStubs(blogJson,
            [new RenderedImage("blog-hero", "https://cdn/x.webp")]);
        Assert.NotNull(updated);
        Assert.Contains("![blog-hero](https://cdn/x.webp)", updated, StringComparison.Ordinal);
        Assert.Contains("![stub:blog-inline-1]()", updated, StringComparison.Ordinal); // untouched slot survives

        // No matching stubs → null (caller skips the write entirely).
        Assert.Null(ImageEndpoints.ReplaceStubs(blogJson, [new RenderedImage("nope", "https://cdn/y.webp")]));
    }

    // ---- Integration: prompts artifact → rendered → blog updated ---------------

    private sealed class FakeRenderer : IImageRenderer
    {
        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct) =>
            Task.FromResult(new byte[] { 1, 2, 3 });

        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct) =>
            Task.FromResult(new byte[] { 1, 2, 3 });
    }

    private sealed class FakePublicStore : IPublicContentStore
    {
        public bool IsConfigured => true;
        public Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct) =>
            Task.FromResult(new Uri($"https://public.example/{path}"));

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    }

    [Fact]
    public async Task Render_images_replaces_blog_stubs_and_bumps_version()
    {
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => new FakeRenderer()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(new FakePublicStore()));
        }));
        var client = app.CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"img-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Img Tester"));
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Img campaign", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;

        // Seed a blog with a stub and an image-prompts artifact directly via the artifact API.
        var blog = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Draft",
                """{"content":{"title":"Draft","markdown":"Hello ![stub:blog-hero]() world"},"validation":{}}"""))).Content
            .ReadFromJsonAsync<ArtifactResponse>())!;
        var prompts = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("image-prompts", "Images",
                """{"content":{"title":"Images","images":[{"slot":"blog-hero","prompt":"a hero image","aspectRatio":"16:9"}]},"validation":{}}"""))).Content
            .ReadFromJsonAsync<ArtifactResponse>())!;

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/render-images",
            new { imagePromptsArtifactId = prompts.Id, blogArtifactId = blog.Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("blog-hero.webp", body, StringComparison.Ordinal);

        var updatedBlog = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{blog.Id}");
        Assert.Contains("https://public.example/", updatedBlog!.ContentJson, StringComparison.Ordinal);
        Assert.DoesNotContain("![stub:blog-hero]()", updatedBlog.ContentJson, StringComparison.Ordinal);
        Assert.Equal(2, updatedBlog.Version);
    }
}
