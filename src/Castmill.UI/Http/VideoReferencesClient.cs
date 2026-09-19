using Castmill.Core.Resources;

namespace Castmill.UI.Http;

/// <summary>Typed client for lossless video-derived reference sets.</summary>
public sealed class VideoReferencesClient(ApiClient api)
{
    public Task<List<VideoReferenceSetResponse>> ListAsync(
        Guid campaignId, Guid? videoAssetId = null, CancellationToken ct = default) =>
        api.GetAsync<List<VideoReferenceSetResponse>>(
            $"api/v1/campaigns/{campaignId}/reference-sets"
            + (videoAssetId is null ? string.Empty : $"?videoAssetId={videoAssetId}"), ct);

    public Task<VideoReferenceSetResponse> CreateAsync(
        Guid campaignId, VideoReferenceSetCreateRequest request, CancellationToken ct = default) =>
        api.PostAsync<VideoReferenceSetCreateRequest, VideoReferenceSetResponse>(
            $"api/v1/campaigns/{campaignId}/reference-sets", request, anonymous: false, ct);

    public Task<VideoReferenceImageResponse> UpdateAsync(
        Guid campaignId, Guid imageId, VideoReferenceImageUpdateRequest request,
        CancellationToken ct = default) =>
        api.PutAsync<VideoReferenceImageUpdateRequest, VideoReferenceImageResponse>(
            $"api/v1/campaigns/{campaignId}/reference-images/{imageId}", request, etag: null, ct);

    public Task<VideoReferenceImageResponse> RestoreFullAsync(
        Guid campaignId, Guid imageId, CancellationToken ct = default) =>
        api.PostAsync<object, VideoReferenceImageResponse>(
            $"api/v1/campaigns/{campaignId}/reference-images/{imageId}/restore-full",
            new { }, anonymous: false, ct);

    public Task<VideoReferenceAiResponse> AnalyzeAsync(
        Guid campaignId, VideoReferenceAiRequest request, CancellationToken ct = default) =>
        api.PostAsync<VideoReferenceAiRequest, VideoReferenceAiResponse>(
            $"api/v1/campaigns/{campaignId}/reference-analysis", request, anonymous: false, ct);

    public Task<List<ReferenceCropPresetResponse>> ListPresetsAsync(
        Guid brandId, CancellationToken ct = default) =>
        api.GetAsync<List<ReferenceCropPresetResponse>>(
            $"api/v1/brands/{brandId}/reference-crop-presets/", ct);

    public Task<ReferenceCropPresetResponse> SavePresetAsync(
        Guid brandId, ReferenceCropPresetRequest request, CancellationToken ct = default) =>
        api.PostAsync<ReferenceCropPresetRequest, ReferenceCropPresetResponse>(
            $"api/v1/brands/{brandId}/reference-crop-presets/", request, anonymous: false, ct);
}
