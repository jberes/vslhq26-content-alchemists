using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Sas;
using Castmill.Api.Data;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Media;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public sealed record ClipJobCreateRequest(
    [property: Required] Guid AssetId,
    [property: Range(0, 86_400)] double InSeconds,
    [property: Range(0, 86_400)] double OutSeconds,
    bool CropVertical,
    bool BurnCaptions,
    [property: MaxLength(200_000)] string? CaptionsSrt);

public sealed record FrameJobCreateRequest(
    [property: Required] Guid AssetId,
    /// <summary>Timestamp to extract, in seconds from the start of the source.</summary>
    [property: Range(0, 86_400)] double AtSeconds);

public sealed record ClipJobCallbackRequest(
    [property: Required] string Token,
    [property: Required] string Status,
    string? OutputBlobPath,
    [property: MaxLength(2000)] string? Error);

/// <summary>
/// B6 clip export: enqueue → Container Apps ffmpeg worker → token-authenticated
/// callback. API instances never run ffmpeg (ADR-008).
/// </summary>
public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/media").RequireAuthorization("TenantAllowed");
        group.MapPost("/clip-jobs", EnqueueAsync).Validate<ClipJobCreateRequest>().RequireRateLimiting("writes");
        group.MapGet("/clip-jobs/{id:guid}", StatusAsync);
        // B9.4: reference frames ride the same queue + worker + status endpoint —
        // one ffmpeg path, not two (ADR-014).
        group.MapPost("/frames", ExtractFrameAsync).Validate<FrameJobCreateRequest>().RequireRateLimiting("writes");

        // Worker callback: anonymous route, authenticated by the per-job token
        // (hash-stored, single job scope) — the worker has no user identity.
        routes.MapPost("/api/v1/media/clip-jobs/{id:guid}/callback", CallbackAsync)
            .Validate<ClipJobCallbackRequest>().AllowAnonymous();
        return routes;
    }

    private static async Task<IResult> EnqueueAsync(
        ClipJobCreateRequest request,
        HttpContext http,
        IClipJobDispatcher queue,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!queue.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for the clip-job queue.");
        }
        if (request.OutSeconds <= request.InSeconds)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["OutSeconds"] = ["OutSeconds must be greater than InSeconds."],
            });
        }
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == request.AssetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var jobId = Guid.NewGuid();
        var callbackToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var outputPath = $"tenants/{asset.TenantId}/clips/{jobId}.mp4";

        var job = new ClipJob
        {
            Id = jobId,
            TenantId = tenant.TenantId!.Value,
            AssetId = asset.Id,
            Mode = "clip",
            InSeconds = request.InSeconds,
            OutSeconds = request.OutSeconds,
            CropVertical = request.CropVertical,
            BurnCaptions = request.BurnCaptions,
            CaptionsSrt = request.CaptionsSrt,
            Status = "Queued",
            OutputBlobPath = outputPath,
            CallbackTokenHash = Hash(callbackToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ClipJobs.Add(job);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(new ClipJobMessage(
            jobId, "clip", asset.BlobPath, outputPath,
            request.InSeconds, request.OutSeconds, request.CropVertical, request.BurnCaptions, request.CaptionsSrt,
            callbackToken,
            CallbackUrl(http, jobId)), ct);

        return Results.Accepted($"/api/v1/media/clip-jobs/{jobId}", new { jobId, status = job.Status });
    }

    /// <summary>
    /// B9.4 — extracts one still for image-to-image reference edits. Async by
    /// design: it is the same ffmpeg job path, so API instances stay off the
    /// transcode (ADR-008/014). Poll GET /clip-jobs/{id} for the SAS download URL.
    /// </summary>
    private static async Task<IResult> ExtractFrameAsync(
        FrameJobCreateRequest request,
        HttpContext http,
        IClipJobDispatcher queue,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!queue.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Storage is not configured for the clip-job queue.");
        }
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == request.AssetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var jobId = Guid.NewGuid();
        var callbackToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var outputPath = $"tenants/{asset.TenantId}/frames/{jobId}.png";

        var job = new ClipJob
        {
            Id = jobId,
            TenantId = tenant.TenantId!.Value,
            AssetId = asset.Id,
            Mode = "frame",
            InSeconds = request.AtSeconds,
            OutSeconds = request.AtSeconds,
            CropVertical = false,
            BurnCaptions = false,
            Status = "Queued",
            OutputBlobPath = outputPath,
            CallbackTokenHash = Hash(callbackToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ClipJobs.Add(job);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(new ClipJobMessage(
            jobId, "frame", asset.BlobPath, outputPath,
            request.AtSeconds, request.AtSeconds, false, false, null,
            callbackToken,
            CallbackUrl(http, jobId)), ct);

        return Results.Accepted($"/api/v1/media/clip-jobs/{jobId}", new { jobId, mode = job.Mode, status = job.Status });
    }

    private static string CallbackUrl(HttpContext http, Guid jobId) =>
        $"{http.Request.Scheme}://{http.Request.Host}/api/v1/media/clip-jobs/{jobId}/callback";

    private static async Task<IResult> StatusAsync(
        Guid id, IBlobSasService sas, CastmillDbContext db, CancellationToken ct)
    {
        var job = await db.ClipJobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }
        string? downloadUrl = null;
        if (job.Status == "Succeeded" && job.OutputBlobPath is not null && sas.IsConfigured)
        {
            downloadUrl = (await sas.MintAsync(job.OutputBlobPath, BlobSasPermissions.Read, null, ct)).ToString();
        }
        return Results.Ok(new { job.Id, job.Status, job.Error, downloadUrl, job.CreatedAt, job.UpdatedAt });
    }

    private static async Task<IResult> CallbackAsync(
        Guid id,
        ClipJobCallbackRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The worker has no tenant context: look the job up unfiltered, then
        // authenticate with a constant-time token-hash comparison.
        var job = await db.ClipJobs.IgnoreQueryFilters().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash(request.Token)),
                Encoding.ASCII.GetBytes(job.CallbackTokenHash)))
        {
            return Results.Unauthorized();
        }
        if (request.Status is not ("Processing" or "Succeeded" or "Failed"))
        {
            return Results.BadRequest();
        }

        job.Status = request.Status;
        job.Error = request.Error;
        job.UpdatedAt = clock.GetUtcNow();
        if (request.Status is "Succeeded" or "Failed")
        {
            // Terminal state: the token is single-use — burn it.
            job.CallbackTokenHash = Hash(Guid.NewGuid().ToString());
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
