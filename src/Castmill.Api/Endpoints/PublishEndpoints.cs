using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Publish;
using Castmill.Api.Services.Secrets;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Endpoints;

public sealed record SchedulePostsRequest(
    [property: Required, MinLength(1)] string[] ChannelIds,
    [property: Required, MinLength(1)] string Text,
    [property: Required] DateTimeOffset ScheduledAt,
    [property: MaxLength(2000)] string? MediaUrl,
    Guid? CampaignId = null);

public static class PublishEndpoints
{
    public static IEndpointRouteBuilder MapPublishEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/publish").RequireAuthorization("TenantAllowed");

        group.MapGet("/readiness", ReadinessAsync);
        group.MapGet("/channels", ChannelsAsync);
        group.MapGet("/queue/{channelId}", QueueAsync);
        group.MapPost("/posts", SchedulePostsAsync).Validate<SchedulePostsRequest>().RequireRateLimiting("writes");
        group.MapDelete("/posts/{postId}", CancelAsync).RequireRateLimiting("writes");
        group.MapPost("/test", TestAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static async Task<IResult> ReadinessAsync(
        ClaimsPrincipal principal,
        IUserSecretsService secrets,
        IOptions<PublishOptions> options,
        CancellationToken ct)
    {
        var configured = options.Value.IsConfigured;
        var statuses = await secrets.StatusAsync(AuthEndpoints.GetUserId(principal), ct);
        var credentialStored = statuses.ContainsKey(SecretKind.BrokerToken);
        var ready = configured && credentialStored;
        var detail = ready
            ? "The publishing broker is configured and its credential is stored server-side."
            : !configured
                ? "No publishing broker has been selected or configured. Posts can be staged in Castmill only."
                : "The publishing broker is configured, but no broker credential is stored. Posts can be staged in Castmill only.";

        return Results.Ok(new PublishReadinessResponse(
            configured, credentialStored, ready, detail, CanSchedule: ready));
    }

    /// <summary>Broker token via secret custody; a clear 503 when the integration isn't set up yet.</summary>
    private static async Task<(string Token, IResult? Error)> ResolveAsync(
        ClaimsPrincipal principal, IUserSecretsService secrets, IOptions<PublishOptions> options, CancellationToken ct)
    {
        if (!options.Value.IsConfigured)
        {
            return (string.Empty, Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Publish:BrokerBaseUrl is not configured."));
        }
        var token = await secrets.GetAsync(AuthEndpoints.GetUserId(principal), SecretKind.BrokerToken, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            return (string.Empty, Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "No broker token stored. Set it via PUT /api/v1/settings/secrets/BrokerToken."));
        }
        return (token, null);
    }

    private static async Task<IResult> ChannelsAsync(
        ClaimsPrincipal principal, IUserSecretsService secrets, IOptions<PublishOptions> options,
        IPublishBrokerClient broker, CancellationToken ct)
    {
        var (token, error) = await ResolveAsync(principal, secrets, options, ct);
        if (error is not null)
        {
            return error;
        }
        return Results.Ok(await broker.ListChannelsAsync(token, ct));
    }

    private static async Task<IResult> QueueAsync(
        string channelId, ClaimsPrincipal principal, IUserSecretsService secrets,
        IOptions<PublishOptions> options, IPublishBrokerClient broker, CancellationToken ct)
    {
        var (token, error) = await ResolveAsync(principal, secrets, options, ct);
        if (error is not null)
        {
            return error;
        }
        return Results.Ok(await broker.GetQueueAsync(token, channelId, ct));
    }

    private static async Task<IResult> SchedulePostsAsync(
        SchedulePostsRequest request,
        ClaimsPrincipal principal,
        IUserSecretsService secrets,
        IOptions<PublishOptions> options,
        IPublishBrokerClient broker,
        ITenantProvider tenant,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (token, error) = await ResolveAsync(principal, secrets, options, ct);
        if (error is not null)
        {
            return error;
        }

        if (request.MediaUrl is { Length: > 0 } mediaUrl
            && (request.CampaignId is not { } campaignId
            || !await db.ImageSlots.AnyAsync(slot =>
                slot.CampaignId == campaignId
                && slot.State == "Filled"
                && slot.PublishedUrl == mediaUrl, ct)))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "Publishing media must be a filled image from the selected campaign.");
        }

        var channels = await broker.ListChannelsAsync(token, ct);
        var selected = channels.Where(channel => request.ChannelIds.Contains(channel.Id, StringComparer.Ordinal)).ToList();
        if (selected.Count != request.ChannelIds.Distinct(StringComparer.Ordinal).Count())
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "One or more publishing channels are no longer available.");
        }
        var overLimit = selected
            .Select(channel => new { channel, overBy = PlatformLimits.OverBy(NormalizePlatform(channel.Platform), request.Text) })
            .FirstOrDefault(item => item.overBy > 0);
        if (overLimit is not null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: $"{overLimit.channel.Name} is {overLimit.overBy} characters over its platform limit.");
        }
            if (PlatformLimits.CharacterCount(request.Text) > 65_000)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "Publishing copy cannot exceed 65,000 Unicode characters.");
            }

        // Per-channel fan-out with partial-failure reporting: every channel gets
        // an explicit outcome; one broken channel never blocks the rest.
        var scheduled = new List<BrokerPost>();
        var failures = new List<object>();
        foreach (var channelId in request.ChannelIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                scheduled.Add(await broker.SchedulePostAsync(
                    token, channelId, request.Text, request.ScheduledAt, request.MediaUrl, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new { channelId, error = ex.GetType().Name });
            }
        }

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            UserId = AuthEndpoints.GetUserId(principal),
            Action = "publish.schedule",
            Detail = $"{scheduled.Count} scheduled, {failures.Count} failed",
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { scheduled, failures });
    }

    private static async Task<IResult> CancelAsync(
        string postId, ClaimsPrincipal principal, IUserSecretsService secrets,
        IOptions<PublishOptions> options, IPublishBrokerClient broker,
        ITenantProvider tenant, CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (token, error) = await ResolveAsync(principal, secrets, options, ct);
        if (error is not null)
        {
            return error;
        }
        await broker.CancelPostAsync(token, postId, ct);

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId!.Value,
            UserId = AuthEndpoints.GetUserId(principal),
            Action = "publish.cancel",
            Detail = postId,
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestAsync(
        ClaimsPrincipal principal, IUserSecretsService secrets, IOptions<PublishOptions> options,
        IPublishBrokerClient broker, CancellationToken ct)
    {
        var (token, error) = await ResolveAsync(principal, secrets, options, ct);
        if (error is not null)
        {
            return error;
        }
        try
        {
            var channels = await broker.ListChannelsAsync(token, ct);
            return Results.Ok(new { ok = true, channelCount = channels.Count });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Ok(new { ok = false, error = ex.GetType().Name });
        }
    }

    private static string NormalizePlatform(string value)
    {
        var platform = value.Trim().ToLowerInvariant();
        return platform is "twitter" or "twitter-x" ? "x" : platform;
    }
}
