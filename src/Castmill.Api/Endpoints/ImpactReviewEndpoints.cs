using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Evidence;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public static class ImpactReviewEndpoints
{
    public static IEndpointRouteBuilder MapImpactReviewEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns/{campaignId:guid}/impact-review")
            .RequireAuthorization("TenantAllowed");
        group.MapGet("/", GetAsync);
        group.MapPost("/{artifactId:guid}/keep", KeepAsync)
            .RequireRateLimiting("writes");
        group.MapPost("/{artifactId:guid}/regenerate", RegenerateAsync)
            .RequireRateLimiting("ai");
        return routes;
    }

    private static async Task<IResult> GetAsync(
        Guid campaignId,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(campaign => campaign.Id == campaignId, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await dependencies.GetImpactReviewAsync(campaignId, ct));
    }

    private static async Task<IResult> KeepAsync(
        Guid campaignId,
        Guid artifactId,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!await db.Artifacts.AnyAsync(
            artifact => artifact.Id == artifactId
                && artifact.CampaignId == campaignId
                && ArtifactKinds.UserContent.Contains(artifact.Kind), ct))
        {
            return Results.NotFound();
        }

        var impact = await dependencies.AcknowledgeAsync(campaignId, artifactId, ct);
        return impact is null
            ? Results.Problem(
                "Approve evidence and an SEO/AEO strategy before acknowledging this artifact.",
                statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(new ContentImpactActionResponse(artifactId, "Kept", impact));
    }

    private static async Task<IResult> RegenerateAsync(
        Guid campaignId,
        Guid artifactId,
        ClaimsPrincipal principal,
        IAiOrchestrator orchestrator,
        IContentDependencyService dependencies,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(
            candidate => candidate.Id == campaignId, ct);
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            candidate => candidate.Id == artifactId
                && candidate.CampaignId == campaignId, ct);
        if (campaign is null || artifact is null || !ArtifactKinds.IsUserContent(artifact.Kind))
        {
            return Results.NotFound();
        }

        var impact = (await dependencies.GetImpactReviewAsync(campaignId, ct)).Artifacts
            .Single(item => item.ArtifactId == artifactId);
        if (!impact.CanRegenerate)
        {
            return Results.Problem(
                impact.ReadinessReason ?? "This artifact cannot be regenerated yet.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var transcriptArtifactId = await db.SourceAssets
            .Where(source => source.CampaignId == campaignId
                && source.Kind == SourceKinds.Transcript
                && source.LegacyArtifactId != null
                && source.ApprovedEvidenceRevision != null)
            .OrderBy(source => source.CreatedAt)
            .Select(source => source.LegacyArtifactId)
            .FirstOrDefaultAsync(ct);
        var transcript = transcriptArtifactId is null
            ? await db.SourceAssets.AnyAsync(source => source.CampaignId == campaignId
                    && source.ApprovedEvidenceRevision != null, ct)
                ? new TranscriptContent("approved evidence", [])
                : null
            : await dependencies.LoadApprovedTranscriptAsync(
                campaignId, transcriptArtifactId.Value, ct);
        if (transcript is null)
        {
            return Results.Problem(
                "This campaign has no approved evidence to regenerate from.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var userId = AuthEndpoints.GetUserId(principal);
        GenerationResult result;
        if (artifact.Kind == "blog")
        {
            result = await orchestrator.RunBlogAsync(
                userId, campaign, transcript, null, ct, artifact.Id);
        }
        else if (Generators.Find(artifact.Kind) is { } generator)
        {
            result = await orchestrator.RunGeneratorAsync(
                userId,
                campaign,
                transcript,
                null,
                generator,
                ct,
                artifact.ParentArtifactId,
                artifact.Id);
        }
        else
        {
            return Results.Problem(
                $"{artifact.Kind} has no registered generator path.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (!result.Success)
        {
            return Results.Problem(
                result.Error ?? "Regeneration failed.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var refreshed = (await dependencies.GetImpactReviewAsync(campaignId, ct)).Artifacts
            .Single(item => item.ArtifactId == artifactId);
        return Results.Ok(new ContentImpactActionResponse(artifactId, "Regenerated", refreshed));
    }
}