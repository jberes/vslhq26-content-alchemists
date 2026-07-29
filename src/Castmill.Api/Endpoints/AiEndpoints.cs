using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Endpoints;

public sealed record TranscribeRequest(
    [property: Required] Guid AssetId,
    /// <summary>Force the Azure Speech long-media path regardless of size.</summary>
    bool UseSpeech = false);

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/ai").RequireAuthorization("TenantAllowed");

        group.MapGet("/status", StatusAsync);
        group.MapGet("/log", Log);
        group.MapPost("/campaigns/{campaignId:guid}/transcripts", IngestTranscriptAsync)
            .Validate<TranscriptIngestRequest>().RequireRateLimiting("writes");
        group.MapPost("/campaigns/{campaignId:guid}/transcribe", TranscribeAsync)
            .Validate<TranscribeRequest>().RequireRateLimiting("ai");
        group.MapPost("/campaigns/{campaignId:guid}/generate", GenerateAsync)
            .Validate<GenerateRequest>().RequireRateLimiting("ai");
        group.MapPost("/campaigns/{campaignId:guid}/generate/{kind}", GenerateOneAsync)
            .Validate<GenerateRequest>().RequireRateLimiting("ai");
        return routes;
    }

    // ---- Status & transparency ----------------------------------------------

    private static async Task<IResult> StatusAsync(
        bool? probe,
        ClaimsPrincipal principal,
        IFoundryClientFactory clients,
        IOptions<AiOptions> options,
        CancellationToken ct)
    {
        var userId = AuthEndpoints.GetUserId(principal);
        var credentials = await clients.ResolveCredentialsAsync(userId, ct);
        var models = options.Value.Models
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        string? probeResult = null;
        if (probe == true && credentials is not null && models.ContainsKey("chat"))
        {
            try
            {
                var client = await clients.CreateChatClientAsync(userId, "chat", ct);
                var response = await client.GetResponseAsync("Reply with the single word: ok", cancellationToken: ct);
                probeResult = string.IsNullOrWhiteSpace(response.Text) ? "empty response" : "ok";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Actionable, but never includes the endpoint/key themselves.
                probeResult = $"probe failed: {ex.GetType().Name}";
            }
        }

        return Results.Ok(new AiStatusResponse(
            credentials?.Source ?? "none",
            credentials is not null,
            models,
            options.Value.Speech.IsConfigured,
            probeResult));
    }

    private static IResult Log(ClaimsPrincipal principal, IPromptLog promptLog) =>
        Results.Ok(promptLog.ForUser(AuthEndpoints.GetUserId(principal)));

    // ---- Transcript ingest ----------------------------------------------------

    private static async Task<IResult> IngestTranscriptAsync(
        Guid campaignId,
        TranscriptIngestRequest request,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        var transcript = TranscriptService.FromPlainText(request.Text, request.Source);
        if (transcript.Segments.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Text"] = ["No usable sentences found in the pasted text."],
            });
        }
        var artifact = await PersistTranscriptAsync(campaignId, transcript, tenant, db, clock, ct);
        return Results.Created(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifact.Id}",
            new { transcriptArtifactId = artifact.Id, segmentCount = transcript.Segments.Count });
    }

    private static async Task<IResult> TranscribeAsync(
        Guid campaignId,
        TranscribeRequest request,
        ClaimsPrincipal principal,
        ITranscriptionService transcription,
        IBlobSasService blobs,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == request.AssetId, ct);
        if (asset is null)
        {
            return Results.NotFound();
        }

        var blob = await blobs.OpenReadAsync(asset.BlobPath, ct);
        if (blob is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "Asset is registered but its file has not been uploaded yet.");
        }

        await using var stream = blob.Value.Stream;
        TranscriptContent transcript;
        try
        {
            if (request.UseSpeech || blob.Value.Length > TranscriptionService.ShortPathMaxBytes)
            {
                transcript = await transcription.TranscribeLongAsync(stream, asset.FileName, ct);
            }
            else
            {
                transcript = await transcription.TranscribeShortAsync(
                    AuthEndpoints.GetUserId(principal), stream, asset.FileName, ct);
            }
        }
        catch (AiNotConfiguredException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }

        var artifact = await PersistTranscriptAsync(campaignId, transcript, tenant, db, clock, ct);
        return Results.Created(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifact.Id}",
            new { transcriptArtifactId = artifact.Id, segmentCount = transcript.Segments.Count });
    }

    // ---- Generation -----------------------------------------------------------

    private static async Task<IResult> GenerateAsync(
        Guid campaignId,
        GenerateRequest request,
        ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        CastmillDbContext db,
        HttpResponse response,
        CancellationToken ct)
    {
        var loaded = await LoadAsync(campaignId, request.TranscriptArtifactId, db, ct);
        if (loaded is not var (campaign, transcript))
        {
            return Results.NotFound();
        }

        var results = await orchestrator.RunFanOutAsync(
            AuthEndpoints.GetUserId(principal), campaign, transcript, request.Brief, request.Kinds, ct);

        // Per-phase costs for the Press Run UI (G7).
        response.Headers.Append("Server-Timing",
            string.Join(", ", results.Select(r => $"{r.Kind};dur={r.DurationMs}")));
        return Results.Ok(new
        {
            succeeded = results.Count(r => r.Success),
            failed = results.Count(r => !r.Success),
            results,
        });
    }

    private static async Task<IResult> GenerateOneAsync(
        Guid campaignId,
        string kind,
        GenerateRequest request,
        ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var loaded = await LoadAsync(campaignId, request.TranscriptArtifactId, db, ct);
        if (loaded is not var (campaign, transcript))
        {
            return Results.NotFound();
        }
        var userId = AuthEndpoints.GetUserId(principal);

        if (kind.Equals("blog", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(await orchestrator.RunBlogAsync(userId, campaign, transcript, request.Brief, ct));
        }
        var spec = Generators.Find(kind);
        if (spec is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(await orchestrator.RunGeneratorAsync(userId, campaign, transcript, request.Brief, spec, ct));
    }

    // ---- Shared ----------------------------------------------------------------

    private static async Task<(Campaign Campaign, TranscriptContent Transcript)?> LoadAsync(
        Guid campaignId, Guid transcriptArtifactId, CastmillDbContext db, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            return null;
        }
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            a => a.Id == transcriptArtifactId && a.CampaignId == campaignId && a.Kind == "transcript", ct);
        if (artifact is null)
        {
            return null;
        }
        var transcript = TranscriptService.Parse(artifact.ContentJson);
        return transcript is null ? null : (campaign, transcript);
    }

    private static async Task<Artifact> PersistTranscriptAsync(
        Guid campaignId, TranscriptContent transcript, ITenantProvider tenant,
        CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            CampaignId = campaignId,
            Kind = "transcript",
            Title = $"Transcript — {transcript.Source}",
            ContentJson = System.Text.Json.JsonSerializer.Serialize(transcript, TranscriptService.Json),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return artifact;
    }
}
