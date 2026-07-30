using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Publish;
using Castmill.Api.Services.Secrets;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Endpoints;

/// <summary>
/// B9.6 — The Wire's schedule mirror (ADR-016). Entries live here so the strip
/// renders on load and survives reload; the broker stays the scheduler of record
/// and wins every reconcile.
/// </summary>
public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/schedule").RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).Validate<ScheduleEntryCreateRequest>().RequireRateLimiting("writes");
        group.MapPatch("/{id:guid}", MoveAsync).Validate<ScheduleEntryMoveRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{id:guid}", CancelAsync).RequireRateLimiting("writes");
        group.MapPost("/reconcile", ReconcileAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static ScheduleEntryResponse ToResponse(ScheduleEntry e) =>
        new(e.Id, e.CampaignId, e.ArtifactId, e.ChannelId, e.BrokerPostId, e.Text, e.MediaUrl,
            e.ScheduledAt, e.Status, e.Error, e.UpdatedAt);

    /// <summary>Week (or any range) query — the Wire's day columns come straight off this.</summary>
    private static async Task<IResult> ListAsync(
        DateTimeOffset? from, DateTimeOffset? to, Guid? campaignId,
        CastmillDbContext db, CancellationToken ct)
    {
        var query = db.ScheduleEntries.AsQueryable();
        if (from is { } f)
        {
            query = query.Where(e => e.ScheduledAt >= f);
        }
        if (to is { } t)
        {
            query = query.Where(e => e.ScheduledAt < t);
        }
        if (campaignId is { } c)
        {
            query = query.Where(e => e.CampaignId == c);
        }
        var entries = await query.OrderBy(e => e.ScheduledAt).Select(e => ToResponse(e)).ToListAsync(ct);
        return Results.Ok(entries);
    }

    private static async Task<IResult> CreateAsync(
        ScheduleEntryCreateRequest request,
        ClaimsPrincipal principal,
        IUserSecretsService secrets,
        IOptions<PublishOptions> options,
        IPublishBrokerClient broker,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == request.CampaignId, ct))
        {
            return Results.NotFound();
        }
        if (request.ArtifactId is { } artifactId
            && !await db.Artifacts.AnyAsync(a => a.Id == artifactId && a.CampaignId == request.CampaignId, ct))
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var entry = new ScheduleEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            CampaignId = request.CampaignId,
            ArtifactId = request.ArtifactId,
            ChannelId = request.ChannelId,
            Text = request.Text,
            MediaUrl = request.MediaUrl,
            ScheduledAt = request.ScheduledAt,
            Status = "Draft",
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (request.PushToBroker)
        {
            // A broker that isn't configured or is having a bad day leaves a Draft
            // entry with the reason on it — the drag gesture is never lost.
            var token = options.Value.IsConfigured
                ? await secrets.GetAsync(AuthEndpoints.GetUserId(principal), SecretKind.BrokerToken, ct)
                : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                entry.Error = "No broker configured or no broker token stored; entry saved locally.";
            }
            else
            {
                try
                {
                    var post = await broker.SchedulePostAsync(
                        token, request.ChannelId, request.Text, request.ScheduledAt, request.MediaUrl, ct);
                    entry.BrokerPostId = post.Id;
                    entry.Status = "Queued";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry.Status = "Error";
                    entry.Error = $"Broker rejected the post: {ex.GetType().Name}";
                }
            }
        }

        db.ScheduleEntries.Add(entry);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = entry.TenantId,
            UserId = AuthEndpoints.GetUserId(principal),
            Action = "schedule.create",
            Detail = $"{entry.ChannelId} @ {entry.ScheduledAt:O} → {entry.Status}",
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/schedule/{entry.Id}", ToResponse(entry));
    }

    /// <summary>Move = cancel-and-reschedule at the broker; the local row keeps its identity.</summary>
    private static async Task<IResult> MoveAsync(
        Guid id,
        ScheduleEntryMoveRequest request,
        ClaimsPrincipal principal,
        IUserSecretsService secrets,
        IOptions<PublishOptions> options,
        IPublishBrokerClient broker,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var entry = await db.ScheduleEntries.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            return Results.NotFound();
        }
        if (entry.Status == "Sent")
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "A sent post cannot be rescheduled.");
        }

        entry.ScheduledAt = request.ScheduledAt;
        entry.UpdatedAt = clock.GetUtcNow();

        if (entry.BrokerPostId is not null)
        {
            var token = options.Value.IsConfigured
                ? await secrets.GetAsync(AuthEndpoints.GetUserId(principal), SecretKind.BrokerToken, ct)
                : null;
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    await broker.CancelPostAsync(token, entry.BrokerPostId, ct);
                    var post = await broker.SchedulePostAsync(
                        token, entry.ChannelId, entry.Text, entry.ScheduledAt, entry.MediaUrl, ct);
                    entry.BrokerPostId = post.Id;
                    entry.Status = "Queued";
                    entry.Error = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry.Status = "Error";
                    entry.Error = $"Broker move failed: {ex.GetType().Name}";
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        ClaimsPrincipal principal,
        IUserSecretsService secrets,
        IOptions<PublishOptions> options,
        IPublishBrokerClient broker,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var entry = await db.ScheduleEntries.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            return Results.NotFound();
        }

        if (entry.BrokerPostId is not null)
        {
            var token = options.Value.IsConfigured
                ? await secrets.GetAsync(AuthEndpoints.GetUserId(principal), SecretKind.BrokerToken, ct)
                : null;
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    await broker.CancelPostAsync(token, entry.BrokerPostId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The local row must not survive a failed remote cancel silently:
                    // keep it, mark it, let the user retry.
                    entry.Status = "Error";
                    entry.Error = $"Broker cancel failed: {ex.GetType().Name}";
                    entry.UpdatedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                        detail: "The broker rejected the cancellation; the entry is marked Error. Retry.");
                }
            }
        }

        db.ScheduleEntries.Remove(entry);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = entry.TenantId,
            UserId = AuthEndpoints.GetUserId(principal),
            Action = "schedule.cancel",
            Detail = entry.BrokerPostId ?? entry.Id.ToString(),
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Pulls broker queue state for the channels we mirror and lets the broker win
    /// (ADR-016): it owns retries, timezones and platform quirks, so its status is
    /// the truth and ours is a cache.
    /// </summary>
    private static async Task<IResult> ReconcileAsync(
        ClaimsPrincipal principal,
        IUserSecretsService secrets,
        IOptions<PublishOptions> options,
        IPublishBrokerClient broker,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!options.Value.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Publish:BrokerBaseUrl is not configured.");
        }
        var token = await secrets.GetAsync(AuthEndpoints.GetUserId(principal), SecretKind.BrokerToken, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "No broker token stored. Set it via PUT /api/v1/settings/secrets/BrokerToken.");
        }

        var entries = await db.ScheduleEntries.Where(e => e.BrokerPostId != null).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var updated = 0;
        var unreachable = new List<string>();

        foreach (var channelId in entries.Select(e => e.ChannelId).Distinct(StringComparer.Ordinal))
        {
            IReadOnlyList<BrokerPost> queue;
            try
            {
                queue = await broker.GetQueueAsync(token, channelId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreachable.Add(channelId);
                continue;
            }

            foreach (var entry in entries.Where(e => e.ChannelId == channelId))
            {
                var post = queue.FirstOrDefault(p => p.Id == entry.BrokerPostId);
                var status = post is null ? "Sent" : MapStatus(post.Status);
                var scheduledAt = post?.ScheduledAt ?? entry.ScheduledAt;
                if (entry.Status == status && entry.ScheduledAt == scheduledAt)
                {
                    continue;
                }
                // Broker wins — including "it left the queue", which means it went out.
                entry.Status = status;
                entry.ScheduledAt = scheduledAt;
                entry.Error = status == "Error" ? entry.Error ?? "Broker reported an error state." : null;
                entry.UpdatedAt = now;
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { reconciled = entries.Count, updated, unreachableChannels = unreachable });
    }

    private static string MapStatus(string brokerStatus) => brokerStatus.ToLowerInvariant() switch
    {
        "sent" or "published" or "complete" or "completed" => "Sent",
        "error" or "failed" => "Error",
        _ => "Queued",
    };
}
