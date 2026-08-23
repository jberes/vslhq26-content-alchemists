using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Endpoints;

public static class MediaUploadEndpoints
{
    internal const int BlockSize = 4 * 1024 * 1024;
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMediaUploadEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns/{campaignId:guid}/media-uploads")
            .RequireAuthorization("TenantAllowed");

        group.MapPost("/", CreateAsync)
            .Validate<MediaUploadCreateRequest>()
            .RequireRateLimiting("writes");
        group.MapGet("/latest", LatestAsync);
        group.MapGet("/{uploadId:guid}", GetAsync);
        group.MapPut("/{uploadId:guid}/blocks/{blockIndex:int}", PutBlockAsync)
            .RequireRateLimiting("writes")
            .DisableAntiforgery();
        group.MapPost("/{uploadId:guid}/commit", CommitAsync)
            .RequireRateLimiting("writes");
        group.MapPost("/{uploadId:guid}/transcribe", TranscribeAsync)
            .Validate<MediaUploadTranscribeRequest>()
            .RequireRateLimiting("ai");
        group.MapDelete("/{uploadId:guid}", CancelAsync)
            .RequireRateLimiting("writes");
        return routes;
    }

    private static async Task<IResult> CreateAsync(
        Guid campaignId,
        MediaUploadCreateRequest request,
        IOptions<StorageOptions> storageOptions,
        ITenantProvider tenant,
        IBlobSasService blobs,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!blobs.IsConfigured)
        {
            return StorageNotConfigured();
        }
        if (!await db.Campaigns.AnyAsync(campaign => campaign.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        if (!IsMediaContentType(request.ContentType))
        {
            return Results.Problem(
                "Only audio and video files can use the media upload path.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.SizeBytes > storageOptions.Value.MaxMediaBytes)
        {
            return Results.Problem(
                $"Media files must be {FormatBytes(storageOptions.Value.MaxMediaBytes)} or smaller.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tenantId = tenant.TenantId!.Value;
        var now = clock.GetUtcNow();
        var assetId = Guid.NewGuid();
        var uploadId = Guid.NewGuid();
        var safeName = SafeFileName(request.FileName);
        var asset = new Asset
        {
            Id = assetId,
            TenantId = tenantId,
            FileName = request.FileName.Trim(),
            ContentType = request.ContentType.Trim().ToLowerInvariant(),
            SizeBytes = request.SizeBytes,
            BlobPath = $"tenants/{tenantId}/media/{uploadId:N}/{safeName}",
            CreatedAt = now,
        };
        var upload = new MediaUpload
        {
            Id = uploadId,
            TenantId = tenantId,
            CampaignId = campaignId,
            AssetId = asset.Id,
            UploadedBytes = 0,
            NextBlockIndex = 0,
            BlockIdsJson = "[]",
            Status = MediaUploadStatus.Uploading,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.Add(UploadLifetime),
        };
        db.Assets.Add(asset);
        db.MediaUploads.Add(upload);
        await db.SaveChangesAsync(ct);
        return Results.Created(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}",
            ToResponse(upload, asset));
    }

    private static async Task<IResult> LatestAsync(
        Guid campaignId, CastmillDbContext db, CancellationToken ct)
    {
        var upload = await db.MediaUploads
            .Where(candidate => candidate.CampaignId == campaignId
                && candidate.Status != MediaUploadStatus.Cancelled)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        return upload is null
            ? Results.NotFound()
            : await ResponseAsync(upload, db, ct);
    }

    private static async Task<IResult> GetAsync(
        Guid campaignId, Guid uploadId, CastmillDbContext db, CancellationToken ct)
    {
        var upload = await FindAsync(campaignId, uploadId, db, ct);
        return upload is null
            ? Results.NotFound()
            : await ResponseAsync(upload, db, ct);
    }

    private static async Task<IResult> PutBlockAsync(
        Guid campaignId,
        Guid uploadId,
        int blockIndex,
        HttpRequest request,
        IBlobSasService blobs,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var upload = await FindAsync(campaignId, uploadId, db, ct);
        if (upload is null)
        {
            return Results.NotFound();
        }
        var asset = await db.Assets.SingleAsync(candidate => candidate.Id == upload.AssetId, ct);
        if (upload.Status != MediaUploadStatus.Uploading)
        {
            return Results.Problem(
                "This upload is not accepting blocks.",
                statusCode: StatusCodes.Status409Conflict);
        }
        if (blockIndex < 0 || blockIndex > upload.NextBlockIndex)
        {
            return Results.Problem(
                $"Upload block {upload.NextBlockIndex} is required next.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var suppliedHash = request.Headers["X-Content-SHA256"].ToString().ToLowerInvariant();
        if (suppliedHash.Length != 64 || suppliedHash.Any(character => !Uri.IsHexDigit(character)))
        {
            return Results.Problem(
                "X-Content-SHA256 must contain the lowercase SHA-256 hash of this block.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var blockOffset = (long)blockIndex * BlockSize;
        var expectedBytes = Math.Min(BlockSize, asset.SizeBytes - blockOffset);
        if (expectedBytes <= 0)
        {
            return Results.Problem(
                "All declared media bytes have already been uploaded.",
                statusCode: StatusCodes.Status409Conflict);
        }
        var bytes = await ReadBlockAsync(request.Body, expectedBytes, ct);
        if (bytes.LongLength != expectedBytes)
        {
            return Results.Problem(
                $"Block {blockIndex} must contain exactly {expectedBytes} bytes.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(suppliedHash)))
        {
            return Results.Problem(
                "The uploaded block checksum does not match X-Content-SHA256.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var blockId = BlockId(blockIndex, bytes);
        var currentIds = ParseBlockIds(upload.BlockIdsJson);
        if (blockIndex < upload.NextBlockIndex)
        {
            return currentIds.ElementAtOrDefault(blockIndex) == blockId
                ? Results.Ok(ToResponse(upload, asset))
                : Results.Problem(
                    "That block index was already uploaded with different content.",
                    statusCode: StatusCodes.Status409Conflict);
        }

        await using (var content = new MemoryStream(bytes, writable: false))
        {
            await blobs.StageBlockAsync(asset.BlobPath, blockId, content, ct);
        }
        currentIds.Add(blockId);
        var nextBytes = upload.UploadedBytes + bytes.LongLength;
        var now = clock.GetUtcNow();
        var updated = await db.MediaUploads
            .Where(candidate => candidate.Id == upload.Id
                && candidate.Status == MediaUploadStatus.Uploading
                && candidate.NextBlockIndex == blockIndex
                && candidate.UploadedBytes == upload.UploadedBytes)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.BlockIdsJson, JsonSerializer.Serialize(currentIds, Json))
                .SetProperty(candidate => candidate.UploadedBytes, nextBytes)
                .SetProperty(candidate => candidate.NextBlockIndex, blockIndex + 1)
                .SetProperty(candidate => candidate.UpdatedAt, now), ct);
        if (updated == 0)
        {
            return Results.Problem(
                "Another block upload advanced this session. Reload its status before continuing.",
                statusCode: StatusCodes.Status409Conflict);
        }
        upload.BlockIdsJson = JsonSerializer.Serialize(currentIds, Json);
        upload.UploadedBytes = nextBytes;
        upload.NextBlockIndex = blockIndex + 1;
        upload.UpdatedAt = now;
        return Results.Ok(ToResponse(upload, asset));
    }

    private static async Task<IResult> CommitAsync(
        Guid campaignId,
        Guid uploadId,
        IBlobSasService blobs,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var upload = await FindAsync(campaignId, uploadId, db, ct);
        if (upload is null)
        {
            return Results.NotFound();
        }
        var asset = await db.Assets.SingleAsync(candidate => candidate.Id == upload.AssetId, ct);
        if (upload.Status is MediaUploadStatus.Committed
            or MediaUploadStatus.Transcribing
            or MediaUploadStatus.Completed)
        {
            return Results.Ok(ToResponse(upload, asset));
        }
        if (upload.Status != MediaUploadStatus.Uploading || upload.UploadedBytes != asset.SizeBytes)
        {
            return Results.Problem(
                $"Upload is incomplete ({upload.UploadedBytes} of {asset.SizeBytes} bytes).",
                statusCode: StatusCodes.Status409Conflict);
        }
        var blockIds = ParseBlockIds(upload.BlockIdsJson);
        if (blockIds.Count != upload.NextBlockIndex)
        {
            return Results.Problem(
                "The upload block manifest is incomplete.",
                statusCode: StatusCodes.Status409Conflict);
        }
        await blobs.CommitBlocksAsync(asset.BlobPath, blockIds, asset.ContentType, ct);
        upload.Status = MediaUploadStatus.Committed;
        upload.Error = null;
        upload.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(upload, asset));
    }

    private static async Task<IResult> TranscribeAsync(
        Guid campaignId,
        Guid uploadId,
        MediaUploadTranscribeRequest request,
        ClaimsPrincipal principal,
        ITranscriptionService transcription,
        IBlobSasService blobs,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var upload = await FindAsync(campaignId, uploadId, db, ct);
        if (upload is null)
        {
            return Results.NotFound();
        }
        var asset = await db.Assets.SingleAsync(candidate => candidate.Id == upload.AssetId, ct);
        if (upload.Status == MediaUploadStatus.Completed)
        {
            return Results.Ok(ToResponse(upload, asset));
        }
        if (upload.Status == MediaUploadStatus.Transcribing)
        {
            return Results.Problem(
                "This media upload is already being transcribed.",
                statusCode: StatusCodes.Status409Conflict);
        }
        if (upload.Status != MediaUploadStatus.Committed)
        {
            return Results.Problem(
                "Commit the complete media upload before transcription.",
                statusCode: StatusCodes.Status409Conflict);
        }

        upload.Status = MediaUploadStatus.Transcribing;
        upload.Error = null;
        upload.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        try
        {
            var opened = await blobs.OpenReadAsync(asset.BlobPath, ct);
            if (opened is null)
            {
                throw new InvalidOperationException("The committed media blob was not found.");
            }
            await using var stream = opened.Value.Stream;
            var transcript = request.UseSpeech || opened.Value.Length > TranscriptionService.ShortPathMaxBytes
                ? await transcription.TranscribeLongAsync(stream, asset.FileName, ct)
                : await transcription.TranscribeShortAsync(
                    AuthEndpoints.GetUserId(principal), stream, asset.FileName, ct);
            if (transcript.Segments.Count == 0)
            {
                throw new InvalidOperationException("The transcription provider returned no speech segments.");
            }
            var artifact = await AiEndpoints.PersistTranscriptAsync(
                campaignId,
                transcript,
                SourceModalities.Media,
                asset,
                tenant,
                db,
                clock,
                ct);
            upload.Status = MediaUploadStatus.Completed;
            upload.TranscriptArtifactId = artifact.Id;
            upload.Error = null;
            upload.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(upload, asset));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ResetAfterTranscriptionFailureAsync(
                upload.Id, null, clock.GetUtcNow(), db);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = ex is AiNotConfiguredException
                ? ex.Message
                : ex is HttpRequestException
                    ? "The transcription provider could not be reached. Retry this upload."
                    : ex is InvalidOperationException
                        ? ex.Message
                        : "The transcription provider rejected this media. Retry, or use another supported recording format.";
            await ResetAfterTranscriptionFailureAsync(
                upload.Id, detail, clock.GetUtcNow(), db);
            return Results.Problem(
                detail,
                statusCode: ex is AiNotConfiguredException
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> CancelAsync(
        Guid campaignId,
        Guid uploadId,
        IBlobSasService blobs,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var upload = await FindAsync(campaignId, uploadId, db, ct);
        if (upload is null)
        {
            return Results.NotFound();
        }
        if (upload.Status == MediaUploadStatus.Completed)
        {
            return Results.Problem(
                "A completed transcript source cannot be cancelled.",
                statusCode: StatusCodes.Status409Conflict);
        }
        var asset = await db.Assets.SingleAsync(candidate => candidate.Id == upload.AssetId, ct);
        upload.Status = MediaUploadStatus.Cancelled;
        upload.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await blobs.DeleteAsync(asset.BlobPath, ct);
        return Results.NoContent();
    }

    private static async Task ResetAfterTranscriptionFailureAsync(
        Guid uploadId, string? error, DateTimeOffset now, CastmillDbContext db)
    {
        await db.MediaUploads
            .Where(upload => upload.Id == uploadId
                && upload.Status == MediaUploadStatus.Transcribing)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(upload => upload.Status, MediaUploadStatus.Committed)
                .SetProperty(upload => upload.Error, error)
                .SetProperty(upload => upload.UpdatedAt, now),
                CancellationToken.None);
    }

    private static async Task<MediaUpload?> FindAsync(
        Guid campaignId, Guid uploadId, CastmillDbContext db, CancellationToken ct) =>
        await db.MediaUploads.SingleOrDefaultAsync(
            upload => upload.Id == uploadId && upload.CampaignId == campaignId,
            ct);

    private static async Task<IResult> ResponseAsync(
        MediaUpload upload, CastmillDbContext db, CancellationToken ct)
    {
        var asset = await db.Assets.SingleAsync(candidate => candidate.Id == upload.AssetId, ct);
        return Results.Ok(ToResponse(upload, asset));
    }

    private static MediaUploadResponse ToResponse(MediaUpload upload, Asset asset) => new(
        upload.Id,
        upload.CampaignId,
        upload.AssetId,
        asset.FileName,
        asset.ContentType,
        asset.SizeBytes,
        upload.UploadedBytes,
        upload.NextBlockIndex,
        BlockSize,
        upload.Status,
        upload.Error,
        upload.TranscriptArtifactId,
        upload.UpdatedAt,
        upload.ExpiresAt);

    private static IResult StorageNotConfigured() => Results.Problem(
        "Storage is not configured for private media uploads.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static bool IsMediaContentType(string contentType) =>
        contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string fileName)
    {
        var filtered = string.Concat(fileName.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'));
        while (filtered.Contains("..", StringComparison.Ordinal))
        {
            filtered = filtered.Replace("..", ".", StringComparison.Ordinal);
        }
        return filtered.TrimStart('.') is { Length: > 0 } value ? value : "media";
    }

    private static async Task<byte[]> ReadBlockAsync(
        Stream source, long expectedBytes, CancellationToken ct)
    {
        using var destination = new MemoryStream((int)expectedBytes);
        var buffer = new byte[64 * 1024];
        while (destination.Length <= expectedBytes)
        {
            var maximum = (int)Math.Min(buffer.Length, expectedBytes + 1 - destination.Length);
            var read = await source.ReadAsync(buffer.AsMemory(0, maximum), ct);
            if (read == 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return destination.ToArray();
    }

    private static List<string> ParseBlockIds(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];

    private static string BlockId(int index, byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        Span<byte> id = stackalloc byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(id, index);
        hash.AsSpan(0, 16).CopyTo(id[4..]);
        return Convert.ToBase64String(id);
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.#} GB"
        : $"{bytes / (1024d * 1024):0.#} MB";
}