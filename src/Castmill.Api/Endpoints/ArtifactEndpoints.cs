using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public static class ArtifactEndpoints
{
    /// <summary>Typed-JSON content cap (ADR-003) — large payloads belong in Blob, not SQL rows.</summary>
    private const int MaxContentBytes = 512_000;

    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns/{campaignId:guid}/artifacts")
            .RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListPreviewAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync).Validate<ArtifactCreateRequest>().RequireRateLimiting("writes");
        group.MapPut("/{id:guid}", UpdateAsync).Validate<ArtifactUpdateRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");
        return routes;
    }

    // ---- ETag helpers: the Version counter is the ETag ----------------------

    private static string ToEtag(long version) => $"\"{version}\"";

    private static bool TryParseIfMatch(HttpRequest request, out long version)
    {
        version = 0;
        var raw = request.Headers.IfMatch.FirstOrDefault();
        return raw is not null && long.TryParse(raw.Trim().Trim('"'), out version);
    }

    private static IResult? ValidateContent(string contentJson)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(contentJson) > MaxContentBytes)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                detail: $"ContentJson exceeds {MaxContentBytes / 1000} KB.");
        }
        try
        {
            using var _ = JsonDocument.Parse(contentJson);
            return null;
        }
        catch (JsonException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ContentJson"] = ["Value must be well-formed JSON."],
            });
        }
    }

    // ---- Endpoints -----------------------------------------------------------

    private static async Task<IResult> ListPreviewAsync(
        Guid campaignId, CastmillDbContext db, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }

        // Preview projection (ADR-003): ContentJson never leaves the database
        // for list views — this is what keeps campaign-open payloads small.
        var previews = await db.Artifacts
            .Where(a => a.CampaignId == campaignId)
            .OrderBy(a => a.Kind).ThenBy(a => a.CreatedAt)
            .Select(a => new ArtifactPreviewResponse(
                a.Id, a.CampaignId, a.Kind, a.Title, a.Version, a.CreatedAt, a.UpdatedAt))
            .ToListAsync(ct);
        return Results.Ok(previews);
    }

    private static async Task<IResult> GetAsync(
        Guid campaignId, Guid id, HttpResponse response, CastmillDbContext db, CancellationToken ct)
    {
        var artifact = await db.Artifacts
            .SingleOrDefaultAsync(a => a.Id == id && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        response.Headers.ETag = ToEtag(artifact.Version);
        return Results.Ok(new ArtifactResponse(
            artifact.Id, artifact.CampaignId, artifact.Kind, artifact.Title,
            artifact.ContentJson, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
    }

    private static async Task<IResult> CreateAsync(
        Guid campaignId,
        ArtifactCreateRequest request,
        HttpResponse response,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        if (ValidateContent(request.ContentJson) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            CampaignId = campaignId,
            Kind = request.Kind,
            Title = request.Title,
            ContentJson = request.ContentJson,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = ToEtag(artifact.Version);
        return Results.Created(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifact.Id}",
            new ArtifactResponse(artifact.Id, campaignId, artifact.Kind, artifact.Title,
                artifact.ContentJson, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
    }

    private static async Task<IResult> UpdateAsync(
        Guid campaignId,
        Guid id,
        ArtifactUpdateRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Optimistic concurrency contract: writes REQUIRE If-Match. A missing
        // header is a client bug (428), a stale one is a lost-update save (412).
        if (!TryParseIfMatch(httpRequest, out var expectedVersion))
        {
            return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired,
                detail: "Artifact updates require an If-Match header with the artifact's ETag.");
        }
        if (ValidateContent(request.ContentJson) is { } invalid)
        {
            return invalid;
        }

        var artifact = await db.Artifacts
            .SingleOrDefaultAsync(a => a.Id == id && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }
        if (artifact.Version != expectedVersion)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                detail: "The artifact changed since it was loaded. Reload to get the latest version.");
        }

        artifact.Title = request.Title;
        artifact.ContentJson = request.ContentJson;
        artifact.Version++;
        artifact.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = ToEtag(artifact.Version);
        return Results.Ok(new ArtifactResponse(artifact.Id, campaignId, artifact.Kind, artifact.Title,
            artifact.ContentJson, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
    }

    private static async Task<IResult> DeleteAsync(
        Guid campaignId, Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var artifact = await db.Artifacts
            .SingleOrDefaultAsync(a => a.Id == id && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        db.Artifacts.Remove(artifact);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
