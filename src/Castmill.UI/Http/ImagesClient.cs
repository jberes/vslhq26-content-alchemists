using Castmill.Core.Ai;
using Castmill.Core.Resources;

namespace Castmill.UI.Http;

/// <summary>Result of a variant generation pass for one slot.</summary>
public sealed record VariantsResult(
    Guid SlotId,
    string Kind,
    IReadOnlyList<ImageVariantResponse> Variants,
    IReadOnlyList<string>? Failures);

/// <summary>Result of placing a variant (and of a headline re-composite).</summary>
public sealed record PlaceResult(ImageSlotResponse Slot, long? BlogVersion, bool? FontFallback);

/// <summary>Typed client for the image plan + studio (backend B9 / ADR-012/013/015).</summary>
public sealed class ImagesClient(ApiClient api)
{
    /// <summary>Reserves the six typed slots. Idempotent — safe on campaigns without a run.</summary>
    public Task<List<ImageSlotResponse>> ReserveAsync(Guid campaignId, CancellationToken ct = default) =>
        api.PostAsync<object, List<ImageSlotResponse>>(
            $"api/v1/campaigns/{campaignId}/image-slots/reserve", new { }, anonymous: false, ct);

    /// <summary>Updates prompt / model / headline / safe-area on a slot.</summary>
    public Task<ImageSlotResponse> PatchAsync(
        Guid campaignId, Guid slotId,
        string? prompt = null, string? modelAlias = null, string? sourceSegmentId = null,
        string? headlineText = null, bool? safeArea = null,
        CancellationToken ct = default) =>
        api.PatchAsync<object, ImageSlotResponse>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}",
            new { prompt, modelAlias, sourceSegmentId, headlineText, safeArea },
            etag: null,
            ct);

    /// <summary>Generates N variants against the slot's model. Live call — costs money.</summary>
    public Task<VariantsResult> GenerateAsync(
        Guid campaignId, Guid slotId, int variants, CancellationToken ct = default) =>
        api.PostAsync<object, VariantsResult>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/generate",
            new { variants },
            anonymous: false,
            ct);

    /// <summary>
    /// Places a variant: crops to the slot's exact dimensions, publishes WebP, replaces the
    /// blog's <c>![stub:kind]()</c> marker in place, composites the thumbnail headline.
    /// </summary>
    public Task<PlaceResult> PlaceAsync(
        Guid campaignId, Guid slotId, string url, Guid? blogArtifactId, CancellationToken ct = default) =>
        api.PostAsync<object, PlaceResult>(
            $"api/v1/campaigns/{campaignId}/image-slots/{slotId}/place",
            new { url, blogArtifactId },
            anonymous: false,
            ct);

    /// <summary>Re-composites the thumbnail headline over the placed base image — no model call.</summary>
    public Task<PlaceResult> CompositeAsync(
        Guid campaignId, Guid slotId, string headline, bool safeArea, CancellationToken ct = default) =>
        api.PostAsync<object, PlaceResult>(
            "api/v1/images/composite",
            new { campaignId, slotId, headline, safeArea },
            anonymous: false,
            ct);

    /// <summary>Provider readiness + model map for the studio's model gating (B9.5).</summary>
    public Task<AiStatusResponse> GetAiStatusAsync(CancellationToken ct = default) =>
        api.GetAsync<AiStatusResponse>("api/v1/ai/status", ct);
}
