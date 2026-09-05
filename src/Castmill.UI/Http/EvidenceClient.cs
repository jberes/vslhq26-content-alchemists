using Castmill.Core.Resources;

namespace Castmill.UI.Http;

public sealed class EvidenceClient(ApiClient api)
{
    /// <summary>Media link (ADR-057): local path + fingerprint and/or the cloud copy's asset id. Null leaves a field, empty clears it.</summary>
    public Task<SourceAssetResponse> SetMediaLinkAsync(
        Guid campaignId, Guid sourceAssetId, string? localPath = null, string? contentHash = null,
        Guid? mediaAssetId = null, CancellationToken ct = default) =>
        api.PatchAsync<SourceMediaLinkRequest, SourceAssetResponse>(
            $"api/v1/campaigns/{campaignId}/sources/{sourceAssetId}/media",
            new SourceMediaLinkRequest(localPath, contentHash, mediaAssetId), etag: null, ct);

    public Task<EvidenceRevisionResponse> ImportWebPageAsync(
        Guid campaignId, string url, string? label = null, CancellationToken ct = default) =>
        api.PostAsync<WebPageSourceImportRequest, EvidenceRevisionResponse>(
            $"api/v1/campaigns/{campaignId}/sources/import/webpage",
            new WebPageSourceImportRequest(url, label), anonymous: false, ct);

    public Task<EvidenceRevisionResponse> ImportDocumentAsync(
        Guid campaignId, Guid assetId, string? label = null, CancellationToken ct = default) =>
        api.PostAsync<DocumentSourceImportRequest, EvidenceRevisionResponse>(
            $"api/v1/campaigns/{campaignId}/sources/import/document",
            new DocumentSourceImportRequest(assetId, label), anonymous: false, ct);

    public Task<EvidenceRevisionResponse> ImportArtifactAsync(
        Guid campaignId, Guid artifactId, Guid? revisionId = null,
        string? label = null, CancellationToken ct = default) =>
        api.PostAsync<ArtifactSourceImportRequest, EvidenceRevisionResponse>(
            $"api/v1/campaigns/{campaignId}/sources/import/artifact",
            new ArtifactSourceImportRequest(artifactId, revisionId, label), anonymous: false, ct);

    public Task<List<SourceAssetResponse>> ListSourcesAsync(
        Guid campaignId, CancellationToken ct = default) =>
        api.GetAsync<List<SourceAssetResponse>>(
            $"api/v1/campaigns/{campaignId}/sources", ct);

    public Task<EvidenceRevisionResponse> GetEvidenceAsync(
        Guid campaignId, Guid sourceAssetId, bool approved = false,
        int? revision = null,
        CancellationToken ct = default) =>
        api.GetAsync<EvidenceRevisionResponse>(
            $"api/v1/campaigns/{campaignId}/sources/{sourceAssetId}/evidence?approved={approved.ToString().ToLowerInvariant()}"
            + (revision is null ? string.Empty : $"&revision={revision}"),
            ct);

    public Task<EvidenceRevisionResponse> ReviseAsync(
        Guid campaignId, Guid sourceAssetId, string stableId,
        string? content, bool? isExcluded, CancellationToken ct = default) =>
        api.PatchAsync<EvidenceBlockRevisionRequest, EvidenceRevisionResponse>(
            $"api/v1/campaigns/{campaignId}/sources/{sourceAssetId}/evidence/{Uri.EscapeDataString(stableId)}",
            new EvidenceBlockRevisionRequest(content, isExcluded),
            etag: null,
            ct);

    public Task<EvidenceRevisionResponse> ApproveAsync(
        Guid campaignId, Guid sourceAssetId, int revision,
        CancellationToken ct = default) =>
        api.PostAsync<object, EvidenceRevisionResponse>(
            $"api/v1/campaigns/{campaignId}/sources/{sourceAssetId}/evidence/{revision}/approve",
            new { }, anonymous: false, ct);
}
