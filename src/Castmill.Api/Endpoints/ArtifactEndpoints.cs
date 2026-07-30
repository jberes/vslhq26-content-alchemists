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
        group.MapPatch("/{id:guid}/status", SetStatusAsync)
            .Validate<ArtifactStatusRequest>().RequireRateLimiting("writes");

        // B9.7 revisions (ADR-017) — the version filmstrip's data source.
        group.MapGet("/{id:guid}/revisions", ListRevisionsAsync);
        group.MapGet("/{id:guid}/revisions/{revisionId:guid}", GetRevisionAsync);
        group.MapPost("/{id:guid}/revisions/{revisionId:guid}/restore", RestoreRevisionAsync)
            .RequireRateLimiting("writes");
        return routes;
    }

    /// <summary>
    /// Snapshots an artifact's current content before it is overwritten and trims
    /// the ring to <see cref="ArtifactRevision.RingSize"/> (ADR-017). Call before
    /// mutating; the caller owns SaveChanges so the snapshot and the write commit together.
    /// </summary>
    internal static async Task SnapshotRevisionAsync(
        CastmillDbContext db, Artifact artifact, string reason, DateTimeOffset now, CancellationToken ct)
    {
        db.ArtifactRevisions.Add(new ArtifactRevision
        {
            Id = Guid.NewGuid(),
            TenantId = artifact.TenantId,
            ArtifactId = artifact.Id,
            Version = artifact.Version,
            Title = artifact.Title,
            ContentJson = artifact.ContentJson,
            Reason = reason,
            CreatedAt = now,
        });

        // Trim in the same unit of work: an unbounded history is a storage leak,
        // and the filmstrip only ever shows the recent takes. RingSize-1 survivors
        // plus the row just added lands exactly on the cap.
        var stale = await db.ArtifactRevisions
            .Where(r => r.ArtifactId == artifact.Id)
            .OrderByDescending(r => r.Version)
            .Skip(ArtifactRevision.RingSize - 1)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            db.ArtifactRevisions.RemoveRange(stale);
        }
    }

    // ---- ETag helpers: the Version counter is the ETag ----------------------


    /// <summary>Parses the computed CitationsJson column into the DTO's list. Cheap: the
    /// strings are tiny ("["s01","s04"]"), and the heavy ContentJson never left SQL.</summary>
    internal static IReadOnlyList<string>? ParseCitations(string? citationsJson)
    {
        if (string.IsNullOrWhiteSpace(citationsJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(citationsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

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
        var rows = await db.Artifacts
            .Where(a => a.CampaignId == campaignId)
            .OrderBy(a => a.Kind).ThenBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.CampaignId, a.Kind, a.Title, a.Status, a.Version, a.CreatedAt, a.UpdatedAt, a.CitationsJson })
            .ToListAsync(ct);
        var previews = rows
            .Select(a => new ArtifactPreviewResponse(
                a.Id, a.CampaignId, a.Kind, a.Title, a.Status, a.Version, a.CreatedAt, a.UpdatedAt,
                ParseCitations(a.CitationsJson)))
            .ToList();
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
            artifact.ContentJson, artifact.Status, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
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
                artifact.ContentJson, artifact.Status, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
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

        var now = clock.GetUtcNow();
        // Snapshot the pre-edit take first (ADR-017) — same transaction as the write.
        await SnapshotRevisionAsync(db, artifact, "manual-save", now, ct);
        artifact.Title = request.Title;
        artifact.ContentJson = request.ContentJson;
        artifact.Version++;
        artifact.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = ToEtag(artifact.Version);
        return Results.Ok(new ArtifactResponse(artifact.Id, campaignId, artifact.Kind, artifact.Title,
            artifact.ContentJson, artifact.Status, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
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

        // Revisions are meaningless without their artifact — clear them in the same write.
        var revisions = await db.ArtifactRevisions.Where(r => r.ArtifactId == id).ToListAsync(ct);
        db.ArtifactRevisions.RemoveRange(revisions);
        db.Artifacts.Remove(artifact);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Revisions (B9.7 / ADR-017) -------------------------------------------

    private static async Task<IResult> ListRevisionsAsync(
        Guid campaignId, Guid id, CastmillDbContext db, CancellationToken ct)
    {
        if (!await db.Artifacts.AnyAsync(a => a.Id == id && a.CampaignId == campaignId, ct))
        {
            return Results.NotFound();
        }
        // Newest first: the filmstrip reads left-to-right from the current take.
        var revisions = await db.ArtifactRevisions
            .Where(r => r.ArtifactId == id)
            .OrderByDescending(r => r.Version)
            .Select(r => new ArtifactRevisionResponse(r.Id, r.ArtifactId, r.Version, r.Title, r.Reason, r.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(revisions);
    }

    private static async Task<IResult> GetRevisionAsync(
        Guid campaignId, Guid id, Guid revisionId, CastmillDbContext db, CancellationToken ct)
    {
        var revision = await db.ArtifactRevisions
            .Where(r => r.Id == revisionId && r.ArtifactId == id)
            .Join(db.Artifacts.Where(a => a.CampaignId == campaignId), r => r.ArtifactId, a => a.Id, (r, _) => r)
            .SingleOrDefaultAsync(ct);
        return revision is null
            ? Results.NotFound()
            : Results.Ok(new ArtifactRevisionDetailResponse(revision.Id, revision.ArtifactId, revision.Version,
                revision.Title, revision.Reason, revision.ContentJson, revision.CreatedAt));
    }

    /// <summary>
    /// Restore is an ordinary ETag-guarded write (ADR-017): the current take is
    /// snapshotted first, so restoring is itself undoable and concurrency rules
    /// don't fork into a second code path.
    /// </summary>
    private static async Task<IResult> RestoreRevisionAsync(
        Guid campaignId,
        Guid id,
        Guid revisionId,
        HttpRequest httpRequest,
        HttpResponse response,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!TryParseIfMatch(httpRequest, out var expectedVersion))
        {
            return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired,
                detail: "Restore requires an If-Match header with the artifact's current ETag.");
        }
        var artifact = await db.Artifacts.SingleOrDefaultAsync(a => a.Id == id && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }
        if (artifact.Version != expectedVersion)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                detail: "The artifact changed since it was loaded. Reload to get the latest version.");
        }
        var revision = await db.ArtifactRevisions.SingleOrDefaultAsync(r => r.Id == revisionId && r.ArtifactId == id, ct);
        if (revision is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        await SnapshotRevisionAsync(db, artifact, "restore", now, ct);
        artifact.Title = revision.Title;
        artifact.ContentJson = revision.ContentJson;
        artifact.Version++;
        artifact.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = ToEtag(artifact.Version);
        return Results.Ok(new ArtifactResponse(artifact.Id, campaignId, artifact.Kind, artifact.Title,
            artifact.ContentJson, artifact.Status, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
    }

    /// <summary>
    /// Moves an artifact between the four review states. A separate action from an ordinary
    /// content save: "mark reviewed" and "edit the copy" are different intents, and the
    /// review gate (roadmap E6.9) hangs off this one.
    ///
    /// Like every other artifact write it is ETag-guarded, so two people cannot both
    /// advance the same artifact from a stale view.
    /// </summary>
    private static async Task<IResult> SetStatusAsync(
        Guid campaignId,
        Guid id,
        ArtifactStatusRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!ArtifactStatus.IsValid(request.Status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Status"] = [$"Status must be one of: {string.Join(", ", ArtifactStatus.All)}."],
            });
        }

        if (!TryParseIfMatch(httpRequest, out var expectedVersion))
        {
            return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired,
                detail: "Artifact status changes require an If-Match header with the artifact's ETag.");
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
                detail: "The artifact changed since you loaded it.");
        }

        artifact.Status = request.Status;
        artifact.Version++;
        artifact.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = ToEtag(artifact.Version);
        return Results.Ok(new ArtifactResponse(artifact.Id, campaignId, artifact.Kind, artifact.Title,
            artifact.ContentJson, artifact.Status, artifact.Version, artifact.CreatedAt, artifact.UpdatedAt));
    }
}
