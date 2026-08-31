using Castmill.Core.Resources;

namespace Castmill.UI.Http;

/// <summary>
/// The campaign <c>Preview</c> payload (backend ADR-012 / frontend G9). One fetch carries
/// the campaign, its artifacts AND its image-slot state, which is what lets the header
/// counter, the front page's "slots waiting" block and Focus Mode's slot list all agree
/// without any surface polling on its own.
/// </summary>
public sealed record CampaignPreview(
    CampaignResponse Campaign,
    IReadOnlyList<ArtifactPreviewResponse> Artifacts,
    IReadOnlyList<ImageSlotResponse> ImageSlots,
    int ImagesFilled,
    int ImagesTotal,
    BrandSummaryResponse? Brand = null,
    IReadOnlyList<SourceAssetResponse>? Sources = null);

/// <summary>Typed client for campaigns, artifacts and the preview projection.</summary>
public sealed class CampaignsClient(ApiClient api)
{
    public Task<List<CampaignResponse>> ListAsync(CancellationToken ct = default) =>
        api.GetAsync<List<CampaignResponse>>("api/v1/campaigns", ct);

    public Task<CampaignResponse> GetAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<CampaignResponse>($"api/v1/campaigns/{id}", ct);

    public Task<CampaignPreview> GetPreviewAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<CampaignPreview>($"api/v1/campaigns/{id}/preview", ct);

    /// <summary>The whole workspace dashboard in one call — never fetch per-campaign
    /// previews to build a cross-campaign surface.</summary>
    public Task<DashboardResponse> GetDashboardAsync(CancellationToken ct = default) =>
        api.GetAsync<DashboardResponse>("api/v1/campaigns/dashboard", ct);

    public Task<ReviewDeskResponse> GetReviewDeskAsync(
        string status, int skip = 0, int take = 12, CancellationToken ct = default) =>
        api.GetAsync<ReviewDeskResponse>(
            $"api/v1/campaigns/review-desk?status={Uri.EscapeDataString(status)}&skip={skip}&take={take}",
            ct);

    public Task<CampaignResponse> CreateAsync(
        string name, string? brief, Guid? brandId = null, IReadOnlyList<CampaignLink>? links = null,
        string? contentType = null,
        string? intent = null, IReadOnlyList<string>? outputRecipe = null,
        bool skipSeoAnalysis = false,
        CancellationToken ct = default) =>
        api.PostAsync<CampaignCreateRequest, CampaignResponse>(
            "api/v1/campaigns",
            new CampaignCreateRequest(
                name, brief, brandId, links, contentType, intent, outputRecipe, skipSeoAnalysis),
            anonymous: false, ct);

    public Task<CampaignResponse> UpdateAsync(
        Guid id, string name, string? brief, Guid? brandId = null, IReadOnlyList<CampaignLink>? links = null,
        string status = "Draft", string? contentType = null,
        string? intent = null, IReadOnlyList<string>? outputRecipe = null,
        bool skipSeoAnalysis = false,
        CancellationToken ct = default) =>
        api.PutAsync<CampaignUpdateRequest, CampaignResponse>(
            $"api/v1/campaigns/{id}",
            new CampaignUpdateRequest(
                name, brief, brandId, links, status, contentType, intent, outputRecipe, skipSeoAnalysis),
            etag: null, ct);

    /// <summary>Deletes the campaign and everything in it — artifacts, revisions, slots,
    /// schedule entries, runs. The server cascades explicitly; there is no undo.</summary>
    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/campaigns/{id}", ct);

    public Task<CampaignSharingResponse> GetSharingAsync(
        Guid id, CancellationToken ct = default) =>
        api.GetAsync<CampaignSharingResponse>($"api/v1/campaigns/{id}/sharing", ct);

    public Task<CampaignSharingResponse> UpdateSharingAsync(
        Guid id, bool domainEnabled, CancellationToken ct = default) =>
        api.PutAsync<CampaignSharingRequest, CampaignSharingResponse>(
            $"api/v1/campaigns/{id}/sharing",
            new CampaignSharingRequest(domainEnabled), etag: null, ct);

    public Task<CampaignCollaboratorResponse> AddCollaboratorAsync(
        Guid id, string email, CancellationToken ct = default) =>
        api.PostAsync<CampaignCollaboratorRequest, CampaignCollaboratorResponse>(
            $"api/v1/campaigns/{id}/collaborators",
            new CampaignCollaboratorRequest(email), anonymous: false, ct);

    public Task RemoveCollaboratorAsync(
        Guid id, Guid collaboratorId, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/campaigns/{id}/collaborators/{collaboratorId}", ct);

    // ---- Artifacts ---------------------------------------------------------

    public Task<List<ArtifactPreviewResponse>> ListArtifactsAsync(Guid campaignId, CancellationToken ct = default) =>
        api.GetAsync<List<ArtifactPreviewResponse>>($"api/v1/campaigns/{campaignId}/artifacts", ct);

    public Task<ArtifactResponse> CreateArtifactAsync(
        Guid campaignId, string kind, string title, string contentJson,
        Guid? parentArtifactId = null, CancellationToken ct = default) =>
        api.PostAsync<ArtifactCreateRequest, ArtifactResponse>(
            $"api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest(kind, title, contentJson, parentArtifactId),
            anonymous: false, ct);

    /// <summary>Loads the full artifact with its ETag — every later save is conditional on it.</summary>
    public Task<(ArtifactResponse Artifact, string? ETag)> GetArtifactAsync(
        Guid campaignId, Guid artifactId, CancellationToken ct = default) =>
        api.GetWithETagAsync<ArtifactResponse>($"api/v1/campaigns/{campaignId}/artifacts/{artifactId}", ct);

    /// <summary>Deletes one artifact and its revisions. Server returns 204; no undo.</summary>
    public Task DeleteArtifactAsync(Guid campaignId, Guid artifactId, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/campaigns/{campaignId}/artifacts/{artifactId}", ct);

    public Task<ArtifactResponse> SaveArtifactAsync(
        Guid campaignId, Guid artifactId, string title, string contentJson, string? etag,
        CancellationToken ct = default) =>
        api.PutAsync<ArtifactUpdateRequest, ArtifactResponse>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}",
            new ArtifactUpdateRequest(title, contentJson), etag, ct);

    // ---- Revisions (ADR-017 / ADR-F14): the version filmstrip's data ---------

    public Task<List<ArtifactRevisionResponse>> ListRevisionsAsync(
        Guid campaignId, Guid artifactId, CancellationToken ct = default) =>
        api.GetAsync<List<ArtifactRevisionResponse>>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/revisions", ct);

    public Task<ArtifactRevisionDetailResponse> GetRevisionAsync(
        Guid campaignId, Guid artifactId, Guid revisionId, CancellationToken ct = default) =>
        api.GetAsync<ArtifactRevisionDetailResponse>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/revisions/{revisionId}", ct);

    /// <summary>Restore is an ordinary ETag-guarded write, so concurrency rules don't fork.</summary>
    public Task<ArtifactResponse> RestoreRevisionAsync(
        Guid campaignId, Guid artifactId, Guid revisionId, string? etag, CancellationToken ct = default) =>
        api.PostWithETagAsync<ArtifactResponse>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/revisions/{revisionId}/restore", etag, ct);

    public Task<ArtifactResponse> SetArtifactStatusAsync(
        Guid campaignId, Guid artifactId, string status, string? etag, CancellationToken ct = default) =>
        api.PatchAsync<ArtifactStatusRequest, ArtifactResponse>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/status",
            new ArtifactStatusRequest(status), etag, ct);
}
