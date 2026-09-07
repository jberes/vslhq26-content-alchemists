using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Images;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// ADR-075 end to end: an Auto slot's prompt IS the written visual brief; the brief is written
/// once per set of inputs, shared by preview and render, forgotten on "rewrite", and never
/// written for a Manual slot. Text-first slots on a spelling model get the exact-text rule.
/// </summary>
[Collection("api")]
public sealed class ImageBriefCachingTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task The_brief_is_written_once_and_shared_by_preview_and_render_until_rewritten()
    {
        var writer = new CountingBriefWriter();
        var renderer = new RecordingRenderer(rendersText: true);
        await using var app = App(writer, renderer);
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);

        var first = await PreviewAsync(client, campaignId, slotId);
        var second = await PreviewAsync(client, campaignId, slotId);
        Assert.Equal(1, writer.Calls);
        Assert.StartsWith("BRIEF #1", first.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("BRIEF #1", second.Prompt, StringComparison.Ordinal);
        Assert.Equal("Auto", first.PromptMode);
        // The writer was told what it is briefing for.
        var request = Assert.Single(writer.Requests);
        Assert.Equal("youtube-thumbnail", request.SlotKind);
        Assert.Equal((1280, 720), (request.TargetWidth, request.TargetHeight));
        Assert.True(request.TextMayBeRendered);
        // A text-first slot with no configured headline, on a model that spells, gets the
        // exact-text rule instead of the no-text rule (ADR-075).
        Assert.Contains("Render ONLY the text the brief", first.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not render any new text", first.Prompt, StringComparison.Ordinal);

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 });
        generate.EnsureSuccessStatusCode();
        Assert.Equal(1, writer.Calls);
        Assert.StartsWith("BRIEF #1", Assert.Single(renderer.Prompts), StringComparison.Ordinal);
        Assert.True(renderer.AllowedText.Single());

        var rewrite = await client.PostAsync($"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/brief/rewrite", null);
        Assert.Equal(HttpStatusCode.NoContent, rewrite.StatusCode);
        var third = await PreviewAsync(client, campaignId, slotId);
        Assert.Equal(2, writer.Calls);
        Assert.StartsWith("BRIEF #2", third.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_an_input_rewrites_the_brief_and_a_configured_headline_forbids_rendered_text()
    {
        var writer = new CountingBriefWriter();
        var renderer = new RecordingRenderer(rendersText: true);
        await using var app = App(writer, renderer);
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);

        await PreviewAsync(client, campaignId, slotId);
        (await client.PatchAsync($"/api/v1/campaigns/{campaignId}/image-slots/{slotId}",
            JsonContent.Create(new { headlineText = "REACT GRID" }))).EnsureSuccessStatusCode();
        var preview = await PreviewAsync(client, campaignId, slotId);

        Assert.Equal(2, writer.Calls);
        Assert.False(writer.Requests[^1].TextMayBeRendered);
        Assert.Contains("Do not render any new text", preview.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manual_slot_never_asks_the_writer()
    {
        var writer = new CountingBriefWriter();
        var renderer = new RecordingRenderer(rendersText: true);
        await using var app = App(writer, renderer);
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);
        (await client.PatchAsync($"/api/v1/campaigns/{campaignId}/image-slots/{slotId}",
            JsonContent.Create(new { promptMode = "Manual", prompt = "a bold thumbnail, exactly this" }))).EnsureSuccessStatusCode();

        var preview = await PreviewAsync(client, campaignId, slotId);
        var generate = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 });
        generate.EnsureSuccessStatusCode();

        Assert.Equal(0, writer.Calls);
        Assert.StartsWith("a bold thumbnail, exactly this", preview.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("a bold thumbnail, exactly this", Assert.Single(renderer.Prompts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_writer_cannot_run_the_deterministic_composer_still_produces_a_prompt()
    {
        var writer = new CountingBriefWriter(answer: null);
        var renderer = new RecordingRenderer(rendersText: false);
        await using var app = App(writer, renderer);
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);

        var preview = await PreviewAsync(client, campaignId, slotId);

        Assert.Equal(1, writer.Calls);
        Assert.NotEmpty(preview.Prompt);
        Assert.DoesNotContain("BRIEF #", preview.Prompt, StringComparison.Ordinal);
        Assert.Contains("COMPOSITION REQUIREMENTS", preview.Prompt, StringComparison.Ordinal);
        Assert.Contains("Do not render any new text", preview.Prompt, StringComparison.Ordinal);
    }

    // ---- setup -----------------------------------------------------------------------------

    private static async Task<ImagePromptPreviewResponse> PreviewAsync(HttpClient client, Guid campaignId, Guid slotId)
    {
        var response = await client.GetAsync($"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/prompt-preview");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImagePromptPreviewResponse>())!;
    }

    private WebApplicationFactory<Program> App(CountingBriefWriter writer, RecordingRenderer renderer) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVisualBriefWriter>(_ => writer));
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => renderer));
            s.Replace(ServiceDescriptor.Scoped<IImageProviderRegistry>(_ => new ReadyImageProviderRegistry()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(new MemoryPublicStore()));
        }));

    private static async Task<(HttpClient Client, Guid CampaignId, Guid SlotId)> SetUpSlotAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"brief-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Brief Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Briefed takes", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var slots = (await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>())!;
        var slot = slots.Single(s => s.Kind == "youtube-thumbnail");
        return (client, campaign.Id, slot.Id);
    }

    private sealed class CountingBriefWriter(string? answer = "brief") : IVisualBriefWriter
    {
        public int Calls { get; private set; }
        public List<VisualBriefRequest> Requests { get; } = [];

        public Task<string?> WriteAsync(Guid userId, VisualBriefRequest request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request);
            return Task.FromResult(answer is null ? null : $"BRIEF #{Calls}: a dark navy studio backdrop, one simplified grid, cyan glow.");
        }
    }

    private sealed class RecordingRenderer(bool rendersText) : IImageRenderer
    {
        public List<string> Prompts { get; } = [];
        public List<bool> AllowedText { get; } = [];

        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(SolidPng());
        }

        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(SolidPng());
        }

        public Task<(byte[] Webp, string Prompt)> RenderExactReportingPromptAsync(
            Guid userId, string prompt, int width, int height, string? modelAlias,
            IReadOnlyList<ImageReference> references, CancellationToken ct, bool allowRenderedText = false)
        {
            Prompts.Add(prompt);
            AllowedText.Add(allowRenderedText);
            return Task.FromResult((SolidPng(), prompt));
        }

        public Task<bool> RendersTextAsync(Guid userId, string? modelAlias, CancellationToken ct) => Task.FromResult(rendersText);

        private static byte[] SolidPng()
        {
            using var bitmap = new SKBitmap(1024, 1024);
            bitmap.Erase(SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap);
            return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }
    }

    private sealed class ReadyImageProviderRegistry : IImageProviderRegistry
    {
        private readonly ReadyImageProvider _provider = new();
        public IImageProvider Resolve(string? modelAliasOrProvider) => _provider;
        public Task<IReadOnlyList<ImageProviderStatus>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImageProviderStatus>>([new ImageProviderStatus("test", true, null, SupportsReferenceImages: true)]);
    }

    private sealed class ReadyImageProvider : IImageProvider
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
        {
            _blobs[path] = bytes.ToArray();
            return Task.FromResult(new Uri($"https://public.example/{path}"));
        }
        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
            Task.FromResult(_blobs.TryGetValue(path, out var bytes) ? bytes : null);
    }
}
