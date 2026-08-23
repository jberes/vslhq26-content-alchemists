using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Castmill.Api.Data;
using Castmill.Api.Services.Blob;
using Castmill.Core;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ExportEndpointTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Campaign_export_requires_a_tenant_authorized_user()
    {
        var response = await factory.CreateClient().GetAsync(
            $"/api/v1/campaigns/{Guid.NewGuid()}/export");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Campaign_export_includes_placed_and_generated_full_size_images()
    {
        var store = new MemoryPublicStore();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(store))));
        var client = await AuthedClientAsync(app);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Image export", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        const string placedUrl = "https://public.example/campaigns/export/placed.webp";
        var artifact = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Illustrated post",
                "{\"content\":{\"markdown\":\"# Illustrated post\\n\\n![Hero](" + placedUrl + ")\"}}")))
            .Content.ReadFromJsonAsync<ArtifactResponse>())!;

        var variantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
            var owner = await db.Artifacts.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == artifact.Id);
            var slot = new ImageSlot
            {
                Id = Guid.NewGuid(),
                TenantId = owner.TenantId,
                CampaignId = campaign.Id,
                ArtifactId = artifact.Id,
                Kind = "blog-header",
                TargetWidth = 1600,
                TargetHeight = 840,
                State = "Filled",
                PublishedUrl = placedUrl,
                BaseImagePath = "campaigns/export/base.webp",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ImageSlots.Add(slot);
            db.ImageVariants.Add(new ImageVariant
            {
                Id = variantId,
                TenantId = owner.TenantId,
                CampaignId = campaign.Id,
                SlotId = slot.Id,
                Url = "https://public.example/campaigns/export/take.webp",
                BlobPath = "campaigns/export/take.webp",
                ThumbUrl = "https://public.example/campaigns/export/take-thumb.webp",
                ThumbBlobPath = "campaigns/export/take-thumb.webp",
                Model = "test-image",
                Prompt = "A grounded header",
                State = "Candidate",
                Width = 1600,
                Height = 840,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        store.Blobs["campaigns/export/placed.webp"] = [1, 2, 3];
        store.Blobs["campaigns/export/take.webp"] = [4, 5, 6];

        var response = await client.GetAsync($"/api/v1/campaigns/{campaign.Id}/export");
        response.EnsureSuccessStatusCode();
        using var archive = new ZipArchive(
            new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);

        Assert.NotNull(archive.GetEntry("images/illustrated-post/blog-header.webp"));
        Assert.NotNull(archive.GetEntry(
            $"images/illustrated-post/blog-header-take-{variantId:N}.webp"));
        Assert.Contains("![Hero](../images/illustrated-post/blog-header.webp)",
            ReadEntry(archive, "blog/illustrated-post.md"), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"included\"", ReadEntry(archive, "manifest.json"),
            StringComparison.Ordinal);
    }

    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"export-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple", "Export Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static string ReadEntry(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class MemoryPublicStore : IPublicContentStore
    {
        public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();
        public bool IsConfigured => true;

        public Task<Uri> PublishAsync(
            string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
            Task.FromResult(Blobs.TryGetValue(path, out var bytes) ? bytes : null);

        public Task DeleteAsync(string path, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}