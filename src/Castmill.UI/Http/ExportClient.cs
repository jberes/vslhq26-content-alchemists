namespace Castmill.UI.Http;

/// <summary>
/// Downloads for roadmap 5.6. Everything goes through <see cref="ApiClient"/> like every
/// other call — the export endpoints are authenticated, so there is no shortcut through a
/// plain link (enforced by HttpChokepointTests anyway).
/// </summary>
public sealed class ExportClient(ApiClient api)
{
    /// <summary><paramref name="format"/> is "md" or "docx".</summary>
    public Task<DownloadedFile> ArtifactAsync(
        Guid campaignId, Guid artifactId, string format, CancellationToken ct = default) =>
        api.DownloadAsync(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/export?format={format}", ct);

    public Task<DownloadedFile> CampaignAsync(Guid campaignId, CancellationToken ct = default) =>
        api.DownloadAsync($"api/v1/campaigns/{campaignId}/export", ct);
}
