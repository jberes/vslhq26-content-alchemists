using System.Security.Cryptography;
using Castmill.Core.Resources;

namespace Castmill.UI.Http;

public sealed class MediaUploadClient(ApiClient api)
{
    public Task<MediaUploadResponse> CreateAsync(
        Guid campaignId,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default) =>
        api.PostAsync<MediaUploadCreateRequest, MediaUploadResponse>(
            $"api/v1/campaigns/{campaignId}/media-uploads",
            new MediaUploadCreateRequest(fileName, contentType, sizeBytes),
            anonymous: false,
            ct);

    public async Task<MediaUploadResponse?> GetLatestAsync(
        Guid campaignId, CancellationToken ct = default)
    {
        try
        {
            return await api.GetAsync<MediaUploadResponse>(
                $"api/v1/campaigns/{campaignId}/media-uploads/latest",
                ct);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public Task<MediaUploadResponse> GetAsync(
        Guid campaignId, Guid uploadId, CancellationToken ct = default) =>
        api.GetAsync<MediaUploadResponse>(
            $"api/v1/campaigns/{campaignId}/media-uploads/{uploadId}",
            ct);

    public Task<MediaUploadResponse> PutBlockAsync(
        Guid campaignId,
        Guid uploadId,
        int blockIndex,
        byte[] bytes,
        CancellationToken ct = default) =>
        api.PutBytesAsync<MediaUploadResponse>(
            $"api/v1/campaigns/{campaignId}/media-uploads/{uploadId}/blocks/{blockIndex}",
            bytes,
            "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ct);

    public Task<MediaUploadResponse> CommitAsync(
        Guid campaignId, Guid uploadId, CancellationToken ct = default) =>
        api.PostAsync<object, MediaUploadResponse>(
            $"api/v1/campaigns/{campaignId}/media-uploads/{uploadId}/commit",
            new { },
            anonymous: false,
            ct);

    public Task<MediaUploadResponse> TranscribeAsync(
        Guid campaignId,
        Guid uploadId,
        bool useSpeech = false,
        CancellationToken ct = default) =>
        api.PostAsync<MediaUploadTranscribeRequest, MediaUploadResponse>(
            $"api/v1/campaigns/{campaignId}/media-uploads/{uploadId}/transcribe",
            new MediaUploadTranscribeRequest(useSpeech),
            anonymous: false,
            ct);

    public Task CancelAsync(
        Guid campaignId, Guid uploadId, CancellationToken ct = default) =>
        api.DeleteAsync(
            $"api/v1/campaigns/{campaignId}/media-uploads/{uploadId}",
            ct);
}