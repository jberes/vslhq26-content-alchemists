using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Resources;

public sealed record MediaUploadCreateRequest(
    [property: Required, MinLength(1), MaxLength(400)] string FileName,
    [property: Required, MinLength(1), MaxLength(200)] string ContentType,
    [property: Range(1, long.MaxValue)] long SizeBytes);

public sealed record MediaUploadResponse(
    Guid Id,
    Guid CampaignId,
    Guid AssetId,
    string FileName,
    string ContentType,
    long TotalBytes,
    long UploadedBytes,
    int NextBlockIndex,
    int BlockSize,
    string Status,
    string? Error,
    Guid? TranscriptArtifactId,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt)
{
    public double Percent => TotalBytes == 0 ? 0 : UploadedBytes * 100d / TotalBytes;
}

public sealed record MediaUploadTranscribeRequest(bool UseSpeech = false);