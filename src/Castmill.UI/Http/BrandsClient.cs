using Castmill.Core.Resources;

namespace Castmill.UI.Http;

public sealed record UploadSas(string UploadUrl, string BlobPath);

public sealed record ReadSas(string ReadUrl);

/// <summary>Typed client for the brand kit: profile + style card, assets, templates —
/// plus the asset-library/SAS calls the kit uploader needs.</summary>
public sealed class BrandsClient(ApiClient api)
{
    public Task<List<BrandProfileDetailResponse>> ListAsync(CancellationToken ct = default) =>
        api.GetAsync<List<BrandProfileDetailResponse>>("api/v1/brands", ct);

    public Task<BrandProfileDetailResponse> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<BrandProfileDetailResponse>($"api/v1/brands/{id}", ct);

    public Task<BrandProfileDetailResponse> CreateAsync(
        string name, BrandStyleCard? styleCard, CancellationToken ct = default) =>
        api.PostAsync<BrandProfileUpsertRequest, BrandProfileDetailResponse>(
            "api/v1/brands", new BrandProfileUpsertRequest(name, styleCard), anonymous: false, ct);

    public Task<BrandProfileDetailResponse> UpdateAsync(
        Guid id, string name, BrandStyleCard? styleCard, CancellationToken ct = default) =>
        api.PutAsync<BrandProfileUpsertRequest, BrandProfileDetailResponse>(
            $"api/v1/brands/{id}", new BrandProfileUpsertRequest(name, styleCard), etag: null, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/brands/{id}", ct);

    // ---- Asset kit --------------------------------------------------------------

    public Task<List<BrandAssetResponse>> ListAssetsAsync(Guid brandId, CancellationToken ct = default) =>
        api.GetAsync<List<BrandAssetResponse>>($"api/v1/brands/{brandId}/assets", ct);

    public Task<BrandAssetResponse> LinkAssetAsync(
        Guid brandId, Guid assetId, string kind, string? label, CancellationToken ct = default) =>
        api.PostAsync<BrandAssetLinkRequest, BrandAssetResponse>(
            $"api/v1/brands/{brandId}/assets", new BrandAssetLinkRequest(assetId, kind, label), anonymous: false, ct);

    public Task UnlinkAssetAsync(Guid brandId, Guid brandAssetId, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/brands/{brandId}/assets/{brandAssetId}", ct);

    // ---- Asset library + SAS (uploading kit images rides the ordinary asset flow) ----

    public Task<AssetResponse> CreateLibraryAssetAsync(
        string fileName, string contentType, long sizeBytes, CancellationToken ct = default) =>
        api.PostAsync<AssetCreateRequest, AssetResponse>(
            "api/v1/assets", new AssetCreateRequest(fileName, contentType, sizeBytes), anonymous: false, ct);

    public Task<UploadSas> MintUploadSasAsync(Guid assetId, CancellationToken ct = default) =>
        api.PostAsync<object, UploadSas>($"api/v1/blob/assets/{assetId}/upload-sas", new { }, anonymous: false, ct);

    public Task<ReadSas> MintReadSasAsync(Guid assetId, CancellationToken ct = default) =>
        api.GetAsync<ReadSas>($"api/v1/blob/assets/{assetId}/read-sas", ct);

    // ---- Templates --------------------------------------------------------------

    public Task<List<BrandTemplateResponse>> ListTemplatesAsync(Guid brandId, CancellationToken ct = default) =>
        api.GetAsync<List<BrandTemplateResponse>>($"api/v1/brands/{brandId}/templates", ct);

    public Task<BrandTemplateResponse> CreateTemplateAsync(
        Guid brandId, BrandTemplateRequest request, CancellationToken ct = default) =>
        api.PostAsync<BrandTemplateRequest, BrandTemplateResponse>(
            $"api/v1/brands/{brandId}/templates", request, anonymous: false, ct);

    public Task<BrandTemplateResponse> UpdateTemplateAsync(
        Guid brandId, Guid templateId, BrandTemplateRequest request, CancellationToken ct = default) =>
        api.PutAsync<BrandTemplateRequest, BrandTemplateResponse>(
            $"api/v1/brands/{brandId}/templates/{templateId}", request, etag: null, ct);

    public Task DeleteTemplateAsync(Guid brandId, Guid templateId, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/brands/{brandId}/templates/{templateId}", ct);
}
