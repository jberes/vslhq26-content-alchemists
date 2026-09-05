using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.UI.Http;

/// <summary>Result of placing a variant (and of a headline re-composite).</summary>
public sealed record PlaceResult(ImageSlotResponse Slot, long? BlogVersion, bool? FontFallback);

public sealed record OverlaySaveResult(ImageSlotResponse Slot, bool? FontFallback);

/// <summary>Where an editor-uploaded image ended up in the public container.</summary>
public sealed record UploadedImage(string Url);

/// <summary>Typed client for the image plan + studio (backend B9 / ADR-012/013/015).</summary>
public sealed class ImagesClient(ApiClient api)
{
    public Task<List<ImageSlotResponse>> ListAsync(Guid campaignId, CancellationToken ct = default) =>
        api.GetAsync<List<ImageSlotResponse>>($"api/v1/campaigns/{campaignId}/image-slots", ct);

    /// <summary>Reserves the six typed slots. Idempotent — safe on campaigns without a run.</summary>
    public Task<List<ImageSlotResponse>> ReserveAsync(Guid campaignId, CancellationToken ct = default) =>
        api.PostAsync<object, List<ImageSlotResponse>>(
            $"api/v1/campaigns/{campaignId}/image-slots/reserve", new { }, anonymous: false, ct);

    public Task<ImageSlotResponse> CreateAsync(
        Guid campaignId, Guid artifactId, string promptMode = "Auto",
        string? prompt = null, CancellationToken ct = default) =>
        api.PostAsync<object, ImageSlotResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots",
            new { artifactId, prompt, promptMode }, anonymous: false, ct);

    /// <summary>Updates prompt / model / headline / safe-area on a slot.</summary>
    public Task<ImageSlotResponse> PatchAsync(
        Guid campaignId, Guid slotId,
        string? prompt = null, string? modelAlias = null, string? sourceSegmentId = null,
        string? headlineText = null, bool? safeArea = null,
        string? promptMode = null, IReadOnlyList<Guid>? referenceAssetIds = null,
        bool? useDefaultModel = null,
        CancellationToken ct = default) =>
        api.PatchAsync<object, ImageSlotResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}",
            new { prompt, modelAlias, sourceSegmentId, headlineText, safeArea, promptMode, referenceAssetIds, useDefaultModel },
            etag: null,
            ct);

    /// <summary>Generates N variants against the slot's model. Live call — costs money.
    /// The result carries persisted variants + a run id pollable at <c>runs/{id}</c>.</summary>
    public Task<VariantBatchResponse> GenerateAsync(
        Guid campaignId, Guid slotId, int variants, string? modelAlias = null,
        bool critique = false, CancellationToken ct = default) =>
        api.PostAsync<object, VariantBatchResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate",
            new { variants, modelAlias, critique },
            anonymous: false,
            ct);

    /// <summary>Compare mode (ADR-054): the same prompt rendered once on each listed model, in one run.</summary>
    public Task<VariantBatchResponse> CompareAsync(
        Guid campaignId, Guid slotId, IReadOnlyList<string> modelAliases, int variantsPerModel = 1,
        CancellationToken ct = default) =>
        api.PostAsync<object, VariantBatchResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate",
            new { variants = variantsPerModel, modelAliases },
            anonymous: false,
            ct);

    /// <summary>Saves the overlay editor's spec (ADR-055); a placed slot is re-composited server-side, free.</summary>
    public Task<OverlaySaveResult> SetOverlayAsync(
        Guid campaignId, Guid slotId, OverlaySpec spec, CancellationToken ct = default) =>
        api.PutAsync<OverlaySpec, OverlaySaveResult>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/overlay", spec, etag: null, ct);

    public Task<ImageSlotResponse> ClearOverlayAsync(Guid campaignId, Guid slotId, CancellationToken ct = default) =>
        api.DeleteAsync<ImageSlotResponse>($"api/v1/campaigns/{campaignId}/image-slots/{slotId}/overlay", ct);

    /// <summary>Region edit (ADR-055): repaint the masked part of a take into a new take with lineage. Live call.</summary>
    public Task<VariantBatchResponse> EditRegionAsync(
        Guid campaignId, Guid slotId, Guid variantId, string maskPngBase64, string instruction,
        int variants = 1, string? modelAlias = null, CancellationToken ct = default) =>
        api.PostAsync<object, VariantBatchResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}/edit",
            new { maskPng = maskPngBase64, instruction, variants, modelAlias },
            anonymous: false,
            ct);

    /// <summary>The exact prompt a generate call would send for this slot right now.</summary>
    public Task<ImagePromptPreviewResponse> GetPromptPreviewAsync(
        Guid campaignId, Guid slotId, string? modelAlias = null, CancellationToken ct = default) =>
        api.GetAsync<ImagePromptPreviewResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/prompt-preview"
            + (string.IsNullOrWhiteSpace(modelAlias) ? string.Empty : $"?model={Uri.EscapeDataString(modelAlias)}"), ct);

    /// <summary>
    /// Generates takes for every pending eligible slot in one durable campaign run. Null
    /// <paramref name="artifactId"/> means the whole campaign; a value narrows the pass to
    /// one explicit content item. Existing takes are not placed or published into content.
    /// </summary>
    public Task<ImageBatchResponse> GeneratePendingAsync(
        Guid campaignId, int variantsPerSlot, Guid? artifactId = null,
        CancellationToken ct = default) =>
        api.PostAsync<object, ImageBatchResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/generate-pending",
            new { variantsPerSlot, artifactId },
            anonymous: false,
            ct);

    /// <summary>Every persisted take for the slot, newest first (discarded hidden by default).</summary>
    public Task<List<ImageVariantResponse>> ListVariantsAsync(
        Guid campaignId, Guid slotId, bool includeDiscarded = false, CancellationToken ct = default) =>
        api.GetAsync<List<ImageVariantResponse>>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants?includeDiscarded={includeDiscarded}", ct);

    public Task<DownloadedFile> DownloadVariantAsync(
        Guid campaignId, Guid slotId, Guid variantId, CancellationToken ct = default) =>
        api.DownloadAsync(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}/download", ct);

    /// <summary>Keep / discard / restore a take. A state flip — the pixels stay.</summary>
    public Task<ImageVariantResponse> SetVariantStateAsync(
        Guid campaignId, Guid slotId, Guid variantId, string state, CancellationToken ct = default) =>
        api.PatchAsync<object, ImageVariantResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}",
            new { state }, etag: null, ct);

    public Task<ImageVariantResponse> LockVariantAsync(
        Guid campaignId, Guid slotId, Guid variantId, CancellationToken ct = default) =>
        api.PutAsync<object, ImageVariantResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}/lock",
            new { }, etag: null, ct);

    public Task UnlockVariantAsync(
        Guid campaignId, Guid slotId, Guid variantId, CancellationToken ct = default) =>
        api.DeleteAsync(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}/lock", ct);

    /// <summary>New take(s) steered from an existing one ("add a face", "warmer background").</summary>
    public Task<VariantBatchResponse> SteerAsync(
        Guid campaignId, Guid slotId, Guid variantId, string note, int variants = 1,
        string? modelAlias = null, CancellationToken ct = default) =>
        api.PostAsync<object, VariantBatchResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}/steer",
            new { note, variants, modelAlias },
            anonymous: false,
            ct);

    /// <summary>
    /// Places a variant into its slot by id: the slot fills, the take flips Kept, the
    /// blog's <c>![stub:kind]()</c> marker is replaced, the headline is composited.
    /// </summary>
    public Task<PlaceResult> PlaceAsync(
        Guid campaignId, Guid slotId, Guid variantId, Guid? blogArtifactId, CancellationToken ct = default) =>
        api.PostAsync<object, PlaceResult>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/place",
            new { variantId, blogArtifactId },
            anonymous: false,
            ct);

    /// <summary>Clears a filled slot back to Empty. The prompt survives — it's the user's work.</summary>
    public Task DeleteAsync(Guid campaignId, Guid slotId, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/campaigns/{campaignId}/image-slots/{slotId}", ct);

    /// <summary>Hard-deletes a take — row and blobs. Discard is the recoverable path.</summary>
    public Task DeleteVariantAsync(Guid campaignId, Guid slotId, Guid variantId, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/campaigns/{campaignId}/image-slots/{slotId}/variants/{variantId}", ct);

    /// <summary>Re-composites the thumbnail headline over the placed base image — no model call.</summary>
    public Task<PlaceResult> CompositeAsync(
        Guid campaignId, Guid slotId, string headline, bool safeArea,
        string? headlineBackground = null, CancellationToken ct = default) =>
        api.PostAsync<object, PlaceResult>(
            "api/v1/images/composite",
            new { campaignId, slotId, headline, safeArea, headlineBackground },
            anonymous: false,
            ct);

    /// <summary>Provider readiness + model map for the studio's model gating (B9.5).</summary>
    public Task<AiStatusResponse> GetAiStatusAsync(CancellationToken ct = default) =>
        api.GetAsync<AiStatusResponse>("api/v1/ai/status", ct);

    /// <summary>
    /// Publishes an image pasted or dropped into the manuscript and returns its URL. The
    /// document only ever holds the URL — base64 images are rejected by the editor because
    /// they would blow the artifact's content cap and every export path.
    /// </summary>
    public Task<UploadedImage> UploadImageAsync(
        Guid campaignId, string fileName, string contentType, string base64, CancellationToken ct = default) =>
        api.PostAsync<object, UploadedImage>(
            $"api/v1/campaigns/{campaignId}/images/upload",
            new { fileName, contentType, base64 },
            anonymous: false,
            ct);
}
