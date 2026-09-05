using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Ai.Agents;
using Castmill.Api.Services.Blob;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// The art-director loop end to end (ADR-058): with <c>critique</c> on, a rejected take is
/// re-rendered with the critic's fix and the verdict rides on the take; with it off, nothing
/// about generation changes — the critic is never even asked.
/// </summary>
[Collection("api")]
public sealed class ImageCriticLoopTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task A_rejected_take_is_re_rendered_with_the_fix_and_carries_the_verdict()
    {
        var renderer = new CountingRenderer();
        var critic = new ScriptedCritic(
            new CriticVerdict(false, 41, ["painted text in the lower third"], "Remove all painted text from the lower third.", Ran: true),
            new CriticVerdict(true, 86, [], null, Ran: true));
        await using var app = App(renderer, critic);
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1, critique = true });
        response.EnsureSuccessStatusCode();
        var batch = (await response.Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        Assert.Equal(2, renderer.Prompts.Count);
        Assert.Contains("Art director's fix for the previous render: Remove all painted text", renderer.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(2, critic.Reviews);
        var take = Assert.Single(batch.Variants);
        Assert.Contains("art director 86/100", take.SteeringNote, StringComparison.Ordinal);
        Assert.Contains("1 re-render", take.SteeringNote, StringComparison.Ordinal);
        // The take's own prompt stays the brief the producer wrote — the fix is a render-time
        // instruction, not a change to the slot.
        Assert.DoesNotContain("Art director's fix", take.Prompt ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_toggle_the_critic_is_never_consulted()
    {
        var renderer = new CountingRenderer();
        var critic = new ScriptedCritic();
        await using var app = App(renderer, critic);
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 2 });
        response.EnsureSuccessStatusCode();
        var batch = (await response.Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        Assert.Equal(2, renderer.Prompts.Count);
        Assert.Equal(0, critic.Reviews);
        Assert.All(batch.Variants, v => Assert.Null(v.SteeringNote));
    }

    [Fact]
    public async Task An_unavailable_critic_keeps_the_first_render()
    {
        var renderer = new CountingRenderer();
        var critic = new ScriptedCritic(new CriticVerdict(true, 0, [], null, Ran: false));
        await using var app = App(renderer, critic);
        var (client, campaignId, slotId) = await SetUpSlotAsync(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1, critique = true });
        response.EnsureSuccessStatusCode();
        var batch = (await response.Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        Assert.Single(renderer.Prompts);
        Assert.Equal("art director unavailable", Assert.Single(batch.Variants).SteeringNote);
    }

    // ---- setup -----------------------------------------------------------------------------

    private WebApplicationFactory<Program> App(CountingRenderer renderer, ScriptedCritic critic) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => renderer));
            s.Replace(ServiceDescriptor.Scoped<IImageCritic>(_ => critic));
            s.Replace(ServiceDescriptor.Scoped<IImageProviderRegistry>(_ => new ReadyImageProviderRegistry()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(new MemoryPublicStore()));
        }));

    private static async Task<(HttpClient Client, Guid CampaignId, Guid SlotId)> SetUpSlotAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"critic-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Critic Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Critic takes", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var slots = (await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>())!;
        var slot = slots.Single(s => s.Kind == "youtube-thumbnail");
        (await client.PatchAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}",
            JsonContent.Create(new { prompt = "a bold thumbnail" }))).EnsureSuccessStatusCode();
        return (client, campaign.Id, slot.Id);
    }

    private sealed class ScriptedCritic(params CriticVerdict[] verdicts) : IImageCritic
    {
        private readonly Queue<CriticVerdict> _queue = new(verdicts);
        public int Reviews { get; private set; }

        public Task<CriticVerdict> ReviewAsync(Guid userId, string brief, string slotKind, byte[] webp, CancellationToken ct)
        {
            Reviews++;
            Assert.NotEmpty(webp);
            return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : new CriticVerdict(true, 90, [], null, Ran: true));
        }
    }

    private sealed class CountingRenderer : IImageRenderer
    {
        public List<string> Prompts { get; } = [];

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
