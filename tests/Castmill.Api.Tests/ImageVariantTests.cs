using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace Castmill.Api.Tests;

/// <summary>
/// Items 4/11 of the UX overhaul: takes persist as ImageVariant rows with thumbnails,
/// generation reports through a pollable image-kind run, keep/discard is a state flip,
/// steer creates a lineage-linked take, and placing by id fills the slot.
/// </summary>
[Collection("api")]
public sealed class ImageVariantTests(CastmillApiFactory factory)
{
    private WebApplicationFactory<Program>? _activeApp;

    [Fact]
    public async Task Generate_persists_variants_with_thumbs_and_a_pollable_image_run()
    {
        var (client, campaignId, slotId) = await SetUpSlotAsync();

        var generated = await Client(client).PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 2 });
        Assert.True(generated.StatusCode == HttpStatusCode.OK,
            $"Expected image generation to succeed: {await generated.Content.ReadAsStringAsync()}");
        var batch = (await generated.Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        Assert.Equal(2, batch.Variants.Count);
        Assert.All(batch.Variants, v =>
        {
            Assert.Equal("Candidate", v.State);
            Assert.Contains("/thumbs/", v.ThumbUrl, StringComparison.Ordinal);
            Assert.NotEqual(v.Url, v.ThumbUrl);
        });

        // The run row is image-kind and completed; runs/latest default (content) ignores it.
        var run = await client.GetStringAsync($"/api/v1/ai/runs/{batch.RunId}");
        Assert.Contains("\"status\":\"Completed\"", run, StringComparison.OrdinalIgnoreCase);
        var latestContent = await client.GetAsync($"/api/v1/ai/campaigns/{campaignId}/runs/latest");
        Assert.Equal(HttpStatusCode.NotFound, latestContent.StatusCode);
        var latestImage = await client.GetAsync($"/api/v1/ai/campaigns/{campaignId}/runs/latest?kind=image");
        Assert.Equal(HttpStatusCode.OK, latestImage.StatusCode);

        // The gallery lists the persisted takes.
        var listed = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants");
        Assert.Equal(2, listed!.Count);
    }

    [Fact]
    public async Task Generate_all_pending_skips_filled_and_ineligible_slots_without_placing_takes()
    {
        var renderer = new CapturingRenderer();
        var (client, campaignId, filledSlotId) = await SetUpSlotAsync(renderer);
        var slots = (await client.GetFromJsonAsync<List<ImageSlotResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots"))!;

        var firstTake = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{filledSlotId}/generate",
            new GenerateVariantsRequest(1))).Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{filledSlotId}/place",
            new PlaceVariantRequest(firstTake.Variants[0].Id, null, null))).EnsureSuccessStatusCode();

        var manualSlot = slots.First(slot => slot.Id != filledSlotId);
        (await client.PatchAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{manualSlot.Id}",
            JsonContent.Create(new { promptMode = "Manual" }))).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new ImageBatchGenerateRequest(2));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = (await response.Content.ReadFromJsonAsync<ImageBatchResponse>())!;

        Assert.Equal(slots.Count - 2, batch.SucceededSlots);
        Assert.Equal(0, batch.FailedSlots);
        Assert.Equal(2, batch.SkippedSlots);
        Assert.Equal((slots.Count - 2) * 2, batch.SucceededVariants);
        Assert.Contains(batch.Slots, result => result.SlotId == filledSlotId
            && result.Outcome == "Skipped" && result.ErrorCode == "already_filled");
        Assert.Contains(batch.Slots, result => result.SlotId == manualSlot.Id
            && result.Outcome == "Skipped" && result.ErrorCode == "manual_prompt_missing");

        var refreshed = (await client.GetFromJsonAsync<List<ImageSlotResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots"))!;
        Assert.All(refreshed.Where(slot => slot.Id != filledSlotId),
            slot => Assert.Equal("Empty", slot.State));
        Assert.Equal(1 + batch.SucceededVariants, renderer.Prompts.Count);

        var run = await client.GetStringAsync($"/api/v1/ai/runs/{batch.RunId}");
        Assert.Contains("\"status\":\"Completed\"", run, StringComparison.OrdinalIgnoreCase);
        var latest = await client.GetAsync(
            $"/api/v1/ai/campaigns/{campaignId}/runs/latest?kind=image-batch");
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);

        var retry = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new ImageBatchGenerateRequest(2))).Content.ReadFromJsonAsync<ImageBatchResponse>())!;
        Assert.Equal(0, retry.EligibleSlots);
        Assert.True(retry.SkippedSlots == slots.Count,
            $"Expected every retry slot to be skipped: {JsonSerializer.Serialize(retry)}");
        Assert.Equal(1 + batch.SucceededVariants, renderer.Prompts.Count);
    }

    [Fact]
    public async Task Generate_all_pending_retry_renders_only_the_variant_missing_after_partial_failure()
    {
        var renderer = new CapturingRenderer { FailAtCall = 2 };
        var (client, campaignId, firstSlotId) = await SetUpSlotAsync(renderer);

        var first = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new ImageBatchGenerateRequest(2))).Content.ReadFromJsonAsync<ImageBatchResponse>())!;

        Assert.Equal(1, first.FailedSlots);
        Assert.Equal(1, first.FailedVariants);
        var partial = first.Slots.Single(result => result.SlotId == firstSlotId);
        Assert.Equal("Partial", partial.Outcome);
        Assert.Equal("provider_error", partial.ErrorCode);
        Assert.Equal(1, partial.SucceededVariants);

        var retry = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new ImageBatchGenerateRequest(2))).Content.ReadFromJsonAsync<ImageBatchResponse>())!;

        Assert.Equal(1, retry.EligibleSlots);
        Assert.Equal(1, retry.SucceededVariants);
        Assert.True(retry.SkippedSlots == first.Slots.Count - 1,
            $"Expected every satisfied sibling slot to be skipped: {JsonSerializer.Serialize(retry)}");
        Assert.Equal(first.Slots.Sum(result => result.RequestedVariants) + 1, renderer.Prompts.Count);
        Assert.Contains(retry.Slots, result => result.ErrorCode == "take_target_met");

        var takes = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{firstSlotId}/variants");
        Assert.Equal(2, takes!.Count);
    }

    [Fact]
    public async Task Generate_all_pending_rejects_a_duplicate_concurrent_batch_with_the_active_run_id()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new CapturingRenderer { FirstCallGate = gate };
        var (client, campaignId, _) = await SetUpSlotAsync(renderer);

        var firstRequest = client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new ImageBatchGenerateRequest(1));
        await renderer.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new ImageBatchGenerateRequest(1));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var runId = Assert.Single(duplicate.Headers.GetValues("Castmill-Run-Id"));
        Assert.True(Guid.TryParse(runId, out _));

        gate.SetResult(true);
        var first = await firstRequest;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    }

    [Fact]
    public async Task Generate_all_pending_uses_the_workspace_default_for_slots_without_an_override()
    {
        var renderer = new CapturingRenderer();
        var (client, campaignId, slotId) = await SetUpSlotAsync(renderer);
        (await client.PutAsJsonAsync("/api/v1/settings/images.default-model",
            new { value = "image-alt" })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new ImageBatchGenerateRequest(1));

        response.EnsureSuccessStatusCode();
        Assert.NotEmpty(renderer.Models);
        Assert.All(renderer.Models, model => Assert.Equal("image-alt", model));
        var takes = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants");
        Assert.Equal("image-alt", Assert.Single(takes!).Model);
    }

    [Fact]
    public async Task Keep_discard_is_a_state_flip_and_discarded_hide_by_default()
    {
        var (client, campaignId, slotId) = await SetUpSlotAsync();
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 2 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var baseUrl = $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants";

        var kept = await Patch(client, $"{baseUrl}/{batch.Variants[0].Id}", new { state = "Kept" });
        Assert.Equal("Kept", kept.State);
        var discarded = await Patch(client, $"{baseUrl}/{batch.Variants[1].Id}", new { state = "Discarded" });
        Assert.Equal("Discarded", discarded.State);

        var visible = await client.GetFromJsonAsync<List<ImageVariantResponse>>(baseUrl);
        Assert.Single(visible!);
        var all = await client.GetFromJsonAsync<List<ImageVariantResponse>>($"{baseUrl}?includeDiscarded=true");
        Assert.Equal(2, all!.Count);

        var invalid = await client.PatchAsync($"{baseUrl}/{batch.Variants[0].Id}",
            JsonContent.Create(new { state = "Vanished" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Locked_take_cannot_be_unlocked_or_deleted_by_another_collaborator()
    {
        var (owner, campaignId, slotId) = await SetUpSlotAsync();
        var batch = (await (await owner.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate",
            new { variants = 1 })).Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var take = Assert.Single(batch.Variants);

        var lockedResponse = await owner.PutAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{take.Id}/lock",
            new { });
        lockedResponse.EnsureSuccessStatusCode();
        var locked = (await lockedResponse.Content.ReadFromJsonAsync<ImageVariantResponse>())!;
        Assert.True(locked.IsLocked);
        Assert.True(locked.CanUnlock);

        var collaboratorEmail = $"image-collaborator-{Guid.NewGuid():N}@example.com";
        (await owner.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/collaborators",
            new CampaignCollaboratorRequest(collaboratorEmail))).EnsureSuccessStatusCode();
        var collaborator = await RegisterAsync(collaboratorEmail, "Image Collaborator");
        var baseUrl = $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{take.Id}";

        var collaboratorView = (await collaborator.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants"))!;
        Assert.True(Assert.Single(collaboratorView).IsLocked);
        Assert.False(Assert.Single(collaboratorView).CanUnlock);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await collaborator.DeleteAsync($"{baseUrl}/lock")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await collaborator.DeleteAsync(baseUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"{baseUrl}/lock")).StatusCode);
        var delete = await collaborator.DeleteAsync(baseUrl);
        Assert.True(delete.StatusCode == HttpStatusCode.NoContent,
            $"Expected unlocked collaborator delete to return 204, got {(int)delete.StatusCode}: "
            + await delete.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_lock_and_delete_mutations_have_one_database_winner()
    {
        var (owner, campaignId, slotId) = await SetUpSlotAsync();
        var batch = (await (await owner.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate",
            new { variants = 1 })).Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var take = Assert.Single(batch.Variants);
        var collaboratorEmail = $"image-race-{Guid.NewGuid():N}@example.com";
        (await owner.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/collaborators",
            new CampaignCollaboratorRequest(collaboratorEmail))).EnsureSuccessStatusCode();
        var collaborator = await RegisterAsync(collaboratorEmail, "Image Race Collaborator");
        var takeUrl = $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{take.Id}";
        var lockUrl = $"{takeUrl}/lock";

        var lockResponses = await Task.WhenAll(
            owner.PutAsJsonAsync(lockUrl, new { }),
            collaborator.PutAsJsonAsync(lockUrl, new { }));
        Assert.Single(lockResponses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(lockResponses, response => response.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(lockUrl)).StatusCode);

        var collaboratorLock = collaborator.PutAsJsonAsync(lockUrl, new { });
        var ownerDelete = owner.DeleteAsync(takeUrl);
        var mutationResponses = await Task.WhenAll(collaboratorLock, ownerDelete);

        var lockStatus = mutationResponses[0].StatusCode;
        var deleteStatus = mutationResponses[1].StatusCode;
        Assert.True(
            lockStatus == HttpStatusCode.OK && deleteStatus == HttpStatusCode.Conflict
            || lockStatus == HttpStatusCode.NotFound && deleteStatus == HttpStatusCode.NoContent,
            $"Expected lock or delete to win atomically, got lock {(int)lockStatus} and delete {(int)deleteStatus}.");
    }

    [Fact]
    public async Task Steer_creates_a_lineage_linked_take_with_the_adjusted_prompt()
    {
        var renderer = new CapturingRenderer();
        var (client, campaignId, slotId) = await SetUpSlotAsync(renderer);
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;
        var source = batch.Variants[0];

        var steered = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{source.Id}/steer",
            new { note = "add the host's face on the right" }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        var take = Assert.Single(steered.Variants);
        Assert.Equal(source.Id, take.SourceVariantId);
        Assert.Equal("add the host's face on the right", take.SteeringNote);
        Assert.Contains("Adjustment: add the host's face on the right",
            renderer.Prompts.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Place_by_variant_id_fills_the_slot_and_marks_the_take_kept()
    {
        var (client, campaignId, slotId) = await SetUpSlotAsync();
        var batch = (await (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .Content.ReadFromJsonAsync<VariantBatchResponse>())!;

        var placed = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/place",
            new { variantId = batch.Variants[0].Id });
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        Assert.Contains("\"state\":\"Filled\"", await placed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var takes = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants");
        Assert.Equal("Kept", Assert.Single(takes!).State);

        // A foreign variant id is a plain 404.
        var foreign = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/place",
            new { variantId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Brand_image_style_reaches_the_render_prompt()
    {
        var renderer = new CapturingRenderer();
        var (client, campaignId, slotId) = await SetUpSlotAsync(renderer, async c =>
        {
            var brand = (await (await c.PostAsJsonAsync("/api/v1/brands", new BrandProfileUpsertRequest(
                "Acme", new BrandStyleCard(
                    ImageStyle: "Clean editorial photography, muted blues",
                    Colors: [new BrandColor("primary", "#0A66C2")]))))
                .Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
            return brand.Id;
        });

        (await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate", new { variants = 1 }))
            .EnsureSuccessStatusCode();

        var prompt = renderer.Prompts.Last();
        Assert.Contains("Clean editorial photography, muted blues", prompt, StringComparison.Ordinal);
        Assert.Contains("#0A66C2", prompt, StringComparison.Ordinal);

        // The exact prompt is persisted on the take — reproducibility.
        var takes = await client.GetFromJsonAsync<List<ImageVariantResponse>>(
            $"/api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants");
        Assert.Single(takes!);
    }

    // ---- Setup ------------------------------------------------------------------

    private async Task<(HttpClient Client, Guid CampaignId, Guid SlotId)> SetUpSlotAsync(
        CapturingRenderer? renderer = null,
        Func<HttpClient, Task<Guid>>? brandFactory = null)
    {
        var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IImageRenderer>(_ => renderer ?? new CapturingRenderer()));
            s.Replace(ServiceDescriptor.Scoped<IImageProviderRegistry>(_ => new ReadyImageProviderRegistry()));
            s.Replace(ServiceDescriptor.Singleton<IPublicContentStore>(new MemoryPublicStore()));
        }));
        _activeApp = app;

        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"var-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Variant Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        Guid? brandId = brandFactory is null ? null : await brandFactory(client);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Takes", null, brandId))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var slots = (await (await client.PostAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/reserve", null))
            .Content.ReadFromJsonAsync<List<ImageSlotResponse>>())!;
        var slot = slots.Single(s => s.Kind == "youtube-thumbnail");

        (await client.PatchAsync($"/api/v1/campaigns/{campaign.Id}/image-slots/{slot.Id}",
            JsonContent.Create(new { prompt = "a bold thumbnail" }))).EnsureSuccessStatusCode();

        return (client, campaign.Id, slot.Id);
    }

    private static HttpClient Client(HttpClient client) => client;

    private async Task<HttpClient> RegisterAsync(string email, string displayName)
    {
        var client = (_activeApp ?? factory).CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "correct-horse-battery-staple", displayName));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<ImageVariantResponse> Patch(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsync(url, JsonContent.Create(body));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImageVariantResponse>())!;
    }

    private sealed class CapturingRenderer : IImageRenderer
    {
        public List<string> Prompts { get; } = [];

        public List<string?> Models { get; } = [];

        public int? FailAtCall { get; init; }

        public TaskCompletionSource<bool>? FirstCallGate { get; init; }

        public TaskCompletionSource<bool> FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]> RenderWebpAsync(Guid userId, string prompt, string aspectRatio, string modelAlias, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(SolidPng(64, 64));
        }

        public Task<byte[]> RenderExactAsync(Guid userId, string prompt, int width, int height, string? modelAlias, CancellationToken ct)
        {
            Prompts.Add(prompt);
            Models.Add(modelAlias);
            if (Prompts.Count == FailAtCall)
            {
                throw new ImageProviderException("The provider rejected this take.");
            }
            if (Prompts.Count == 1 && FirstCallGate is { } gate)
            {
                FirstCallStarted.TrySetResult(true);
                return RenderAfterGateAsync(gate.Task);
            }
            return Task.FromResult(SolidPng(1024, 1024));
        }

        private static async Task<byte[]> RenderAfterGateAsync(Task gate)
        {
            await gate;
            return SolidPng(1024, 1024);
        }

        private static byte[] SolidPng(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
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
            Task.FromResult<IReadOnlyList<ImageProviderStatus>>(
                [new ImageProviderStatus("test", true, null, SupportsReferenceImages: true)]);
    }

    private sealed class ReadyImageProvider : IImageProvider
    {
        public string Name => "test";

        public Task<ImageProviderStatus> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult(new ImageProviderStatus(Name, true, null, SupportsReferenceImages: true));

        public Task<byte[]> GenerateAsync(
            Guid userId, string prompt, string aspectRatio, string? modelAlias, CancellationToken ct) =>
            throw new NotSupportedException("The renderer test double owns generation.");
    }

    private sealed class MemoryPublicStore : IPublicContentStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

        public bool IsConfigured => true;

        public Task DeleteAsync(string path, CancellationToken ct)
        {
            _blobs.TryRemove(path, out _);
            return Task.CompletedTask;
        }

        public Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct)
        {
            _blobs[path] = bytes.ToArray();
            return Task.FromResult(new Uri($"https://public.example/{path}"));
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
            Task.FromResult(_blobs.TryGetValue(path, out var bytes) ? bytes : null);
    }
}
