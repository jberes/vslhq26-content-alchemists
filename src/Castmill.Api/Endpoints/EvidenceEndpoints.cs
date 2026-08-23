using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Evidence;
using Castmill.Core;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapEvidenceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns/{campaignId:guid}/sources")
            .RequireAuthorization("TenantAllowed");

        group.MapGet("/", ListSourcesAsync);
        group.MapPost("/import/webpage", ImportWebPageAsync)
            .Validate<WebPageSourceImportRequest>()
            .RequireRateLimiting("writes");
        group.MapPost("/import/document", ImportDocumentAsync)
            .Validate<DocumentSourceImportRequest>()
            .RequireRateLimiting("writes");
        group.MapPost("/import/artifact", ImportArtifactAsync)
            .Validate<ArtifactSourceImportRequest>()
            .RequireRateLimiting("writes");
        group.MapGet("/{sourceAssetId:guid}/evidence", GetEvidenceAsync);
        group.MapPatch("/{sourceAssetId:guid}/evidence/{stableId}", ReviseEvidenceAsync)
            .Validate<EvidenceBlockRevisionRequest>()
            .RequireRateLimiting("writes");
        group.MapPost("/{sourceAssetId:guid}/evidence/{revision:int}/approve", ApproveEvidenceAsync)
            .RequireRateLimiting("writes");
        group.MapGet("/citations/{stableId}", ResolveCitationAsync);

        return routes;
    }

    private static Task<IResult> ImportWebPageAsync(
        Guid campaignId,
        WebPageSourceImportRequest request,
        ISourceImportService imports,
        CancellationToken ct) =>
        ImportAsync(
            campaignId,
            token => imports.ImportWebPageAsync(campaignId, request.Url, request.Label, token),
            ct);

    private static Task<IResult> ImportDocumentAsync(
        Guid campaignId,
        DocumentSourceImportRequest request,
        ISourceImportService imports,
        CancellationToken ct) =>
        ImportAsync(
            campaignId,
            token => imports.ImportDocumentAsync(campaignId, request.AssetId, request.Label, token),
            ct);

    private static Task<IResult> ImportArtifactAsync(
        Guid campaignId,
        ArtifactSourceImportRequest request,
        ISourceImportService imports,
        CancellationToken ct) =>
        ImportAsync(
            campaignId,
            token => imports.ImportArtifactAsync(
                campaignId, request.ArtifactId, request.RevisionId, request.Label, token),
            ct);

    private static async Task<IResult> ImportAsync(
        Guid campaignId,
        Func<CancellationToken, Task<SourceImportResult>> import,
        CancellationToken ct)
    {
        try
        {
            var result = await import(ct);
            return Results.Ok(ToRevisionResponse(
                result.Source,
                result.Source.CurrentEvidenceRevision,
                result.Blocks.ToList()));
        }
        catch (Exception ex) when (ex is SourceImportException or PublicUrlException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Couldn't reach that source.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem(
                "The source import timed out.",
                statusCode: StatusCodes.Status408RequestTimeout);
        }
    }

    private static async Task<IResult> ListSourcesAsync(
        Guid campaignId,
        CastmillDbContext db,
        CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(campaign => campaign.Id == campaignId, ct))
        {
            return Results.NotFound();
        }

        var sources = await db.SourceAssets
            .Where(source => source.CampaignId == campaignId)
            .OrderBy(source => source.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(sources.Select(ToSourceResponse).ToList());
    }

    private static async Task<IResult> GetEvidenceAsync(
        Guid campaignId,
        Guid sourceAssetId,
        bool? approved,
        int? revision,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var source = await db.SourceAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == sourceAssetId && candidate.CampaignId == campaignId,
                ct);
        if (source is null)
        {
            return Results.NotFound();
        }

        var approvedOnly = approved == true;
        var selectedRevision = revision ?? (approvedOnly
            ? source.ApprovedEvidenceRevision
            : source.CurrentEvidenceRevision);
        if (selectedRevision is null)
        {
            return Results.Problem(
                "This source does not have approved evidence yet.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var query = db.EvidenceBlocks.Where(block =>
            block.SourceAssetId == source.Id && block.Revision == selectedRevision.Value);
        var historicalProjection = revision is not null
            && revision != source.CurrentEvidenceRevision;
        if (approvedOnly || historicalProjection)
        {
            query = query.Where(block =>
                block.ApprovalState == EvidenceApprovalStates.Approved
                && !block.IsExcluded);
        }

        var blocks = await query
            .OrderBy(block => block.Ordinal)
            .ThenBy(block => block.StableId)
            .ToListAsync(ct);
        var isApprovedProjection = source.ApprovedEvidenceRevision == selectedRevision;
        return blocks.Count == 0 && !isApprovedProjection
            ? Results.NotFound()
            : Results.Ok(ToRevisionResponse(source, selectedRevision.Value, blocks));
    }

    private static async Task<IResult> ReviseEvidenceAsync(
        Guid campaignId,
        Guid sourceAssetId,
        string stableId,
        EvidenceBlockRevisionRequest request,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.Content is null && request.IsExcluded is null)
        {
            return Results.Problem(
                "Supply corrected content, an exclusion state, or both.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.Content is not null && string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.Problem(
                "Evidence content cannot be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var source = await db.SourceAssets.SingleOrDefaultAsync(
            candidate => candidate.Id == sourceAssetId && candidate.CampaignId == campaignId,
            ct);
        if (source is null)
        {
            return Results.NotFound();
        }

        var current = await db.EvidenceBlocks
            .Where(block => block.SourceAssetId == source.Id
                && block.Revision == source.CurrentEvidenceRevision)
            .OrderBy(block => block.Ordinal)
            .ThenBy(block => block.StableId)
            .AsNoTracking()
            .ToListAsync(ct);
        if (!current.Any(block => string.Equals(block.StableId, stableId, StringComparison.Ordinal)))
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var nextRevision = source.CurrentEvidenceRevision + 1;
        var nextRevisionId = Guid.NewGuid();
        var revised = current.Select(block =>
        {
            var isTarget = string.Equals(block.StableId, stableId, StringComparison.Ordinal);
            var content = isTarget && request.Content is not null
                ? request.Content.Trim()
                : block.Content;
            return new EvidenceBlock
            {
                Id = Guid.NewGuid(),
                TenantId = block.TenantId,
                CampaignId = block.CampaignId,
                SourceAssetId = block.SourceAssetId,
                StableId = block.StableId,
                Ordinal = block.Ordinal,
                Content = content,
                ContentHash = EvidenceRevisionHasher.HashContent(content),
                LocatorKind = block.LocatorKind,
                LocatorJson = block.LocatorJson,
                Revision = nextRevision,
                RevisionId = nextRevisionId,
                ApprovalState = EvidenceApprovalStates.Draft,
                IsExcluded = isTarget && request.IsExcluded.HasValue
                    ? request.IsExcluded.Value
                    : block.IsExcluded,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }).ToList();

        source.CurrentEvidenceRevision = nextRevision;
        source.CurrentEvidenceRevisionId = nextRevisionId;
        source.UpdatedAt = now;
        db.EvidenceBlocks.AddRange(revised);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Results.Problem(
                "The evidence changed while this revision was being created. Reload and try again.",
                statusCode: StatusCodes.Status409Conflict);
        }
        return Results.Ok(ToRevisionResponse(source, nextRevision, revised));
    }

    private static async Task<IResult> ApproveEvidenceAsync(
        Guid campaignId,
        Guid sourceAssetId,
        int revision,
        CastmillDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var source = await db.SourceAssets.SingleOrDefaultAsync(
            candidate => candidate.Id == sourceAssetId && candidate.CampaignId == campaignId,
            ct);
        if (source is null)
        {
            return Results.NotFound();
        }
        if (revision != source.CurrentEvidenceRevision)
        {
            return Results.Problem(
                "Only the current evidence revision can be approved.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var blocks = await db.EvidenceBlocks
            .Where(block => block.SourceAssetId == source.Id && block.Revision == revision)
            .OrderBy(block => block.Ordinal)
            .ThenBy(block => block.StableId)
            .AsNoTracking()
            .ToListAsync(ct);
        if (blocks.Count == 0)
        {
            return Results.NotFound();
        }

        if (source.ApprovedEvidenceRevision == revision
            && source.ApprovedEvidenceRevisionId == source.CurrentEvidenceRevisionId)
        {
            return Results.Ok(ToRevisionResponse(
                source, revision, blocks.Where(block => !block.IsExcluded).ToList()));
        }

        var now = clock.GetUtcNow();
        var approvedHash = EvidenceRevisionHasher.HashApproved(blocks);
        var strategy = db.Database.CreateExecutionStrategy();
        var committed = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var updated = await db.SourceAssets
                .Where(candidate => candidate.Id == source.Id
                    && candidate.CampaignId == campaignId
                    && candidate.CurrentEvidenceRevision == revision
                    && candidate.CurrentEvidenceRevisionId == source.CurrentEvidenceRevisionId
                    && (candidate.ApprovedEvidenceRevision != revision
                        || candidate.ApprovedEvidenceRevisionId != source.CurrentEvidenceRevisionId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.ApprovedEvidenceRevision, revision)
                    .SetProperty(candidate => candidate.ApprovedEvidenceRevisionId,
                        source.CurrentEvidenceRevisionId)
                    .SetProperty(candidate => candidate.ApprovedEvidenceHash, approvedHash)
                    .SetProperty(candidate => candidate.ApprovedAt, now)
                    .SetProperty(candidate => candidate.UpdatedAt, now),
                    ct);
            if (updated == 0)
            {
                await transaction.RollbackAsync(ct);
                return await db.SourceAssets.AsNoTracking().AnyAsync(candidate =>
                    candidate.Id == source.Id
                    && candidate.CampaignId == campaignId
                    && candidate.CurrentEvidenceRevision == revision
                    && candidate.CurrentEvidenceRevisionId == source.CurrentEvidenceRevisionId
                    && candidate.ApprovedEvidenceRevision == revision
                    && candidate.ApprovedEvidenceRevisionId == source.CurrentEvidenceRevisionId,
                    ct);
            }

            await db.EvidenceBlocks
                .Where(block => block.SourceAssetId == source.Id && block.Revision == revision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(block => block.ApprovalState, EvidenceApprovalStates.Approved)
                    .SetProperty(block => block.UpdatedAt, now),
                    ct);
            await CampaignEndpoints.MarkLatestReportStaleAsync(
                campaignId, db, now, inputs: true, ct: ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        });
        if (!committed)
        {
            return Results.Problem(
                "The evidence changed before approval committed. Reload and approve the current revision.",
                statusCode: StatusCodes.Status409Conflict);
        }

        foreach (var block in blocks)
        {
            block.ApprovalState = EvidenceApprovalStates.Approved;
            block.UpdatedAt = now;
        }
        source.ApprovedEvidenceRevision = revision;
        source.ApprovedEvidenceRevisionId = source.CurrentEvidenceRevisionId;
        source.ApprovedEvidenceHash = approvedHash;
        source.ApprovedAt = now;
        source.UpdatedAt = now;
        var committedSource = await db.SourceAssets.AsNoTracking().SingleAsync(
            candidate => candidate.Id == source.Id && candidate.CampaignId == campaignId,
            ct);
        return Results.Ok(ToRevisionResponse(
            committedSource,
            revision,
            blocks.Where(block => !block.IsExcluded).ToList()));
    }

    private static async Task<IResult> ResolveCitationAsync(
        Guid campaignId,
        string stableId,
        Guid? sourceAssetId,
        CastmillDbContext db,
        CancellationToken ct)
    {
        var reference = new CitationReference(stableId, sourceAssetId);
        if (CitationReferenceCodec.TryParse(stableId, out var qualified))
        {
            if (sourceAssetId is not null && sourceAssetId != qualified.SourceAssetId)
            {
                return Results.Problem(
                    "The qualified citation and sourceAssetId identify different sources.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            reference = qualified;
            stableId = qualified.EvidenceBlockId;
            sourceAssetId = qualified.SourceAssetId;
        }

        if (!await db.Campaigns.AnyAsync(campaign => campaign.Id == campaignId, ct))
        {
            return Results.NotFound();
        }

        var matches = await (
            from block in db.EvidenceBlocks
            join source in db.SourceAssets on block.SourceAssetId equals source.Id
            where source.CampaignId == campaignId
                && source.ApprovedEvidenceRevision != null
                && block.Revision == source.ApprovedEvidenceRevision
                && block.StableId == stableId
                && !block.IsExcluded
                && (sourceAssetId == null || source.Id == sourceAssetId)
            orderby source.CreatedAt
            select new { Source = source, Block = block })
            .Take(2)
            .ToListAsync(ct);
        if (matches.Count == 0)
        {
            return Results.Ok(new CitationResolutionResponse(reference, false, null, null, null));
        }
        if (matches.Count > 1)
        {
            return Results.Problem(
                "This legacy citation exists in more than one source; supply sourceAssetId.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var match = matches[0];
        return Results.Ok(new CitationResolutionResponse(
            reference,
            true,
            match.Source.Label,
            ToApprovedRevision(match.Source),
            ToBlockResponse(match.Block)));
    }

    internal static SourceAssetResponse ToSourceResponse(SourceAsset source) => new(
        source.Id,
        source.CampaignId,
        source.LegacyArtifactId,
        source.Kind,
        source.Modality,
        source.Label,
        source.OriginalUri,
        source.ContentType,
        source.SizeBytes,
        source.SnapshotIdentity,
        source.CurrentEvidenceRevision,
        source.CurrentEvidenceRevisionId,
        ToApprovedRevision(source),
        source.CreatedAt,
        source.UpdatedAt);

    private static ApprovedEvidenceRevision? ToApprovedRevision(SourceAsset source) =>
        source.ApprovedEvidenceRevision is { } revision
        && source.ApprovedEvidenceRevisionId is { } revisionId
        && source.ApprovedEvidenceHash is { } hash
        && source.ApprovedAt is { } approvedAt
            ? new ApprovedEvidenceRevision(source.Id, revision, revisionId, hash, approvedAt)
            : null;

    private static EvidenceRevisionResponse ToRevisionResponse(
        SourceAsset source,
        int revision,
        List<EvidenceBlock> blocks)
    {
        var revisionId = blocks.Count > 0
            ? blocks[0].RevisionId
            : source.ApprovedEvidenceRevision == revision
                ? source.ApprovedEvidenceRevisionId ?? source.CurrentEvidenceRevisionId
                : source.CurrentEvidenceRevisionId;
        return new EvidenceRevisionResponse(
            ToSourceResponse(source),
            revision,
            revisionId,
            (source.ApprovedEvidenceRevision == revision
                && source.ApprovedEvidenceRevisionId == revisionId)
            || (blocks.Count > 0 && blocks.All(block =>
                block.ApprovalState == EvidenceApprovalStates.Approved)),
            blocks.Select(ToBlockResponse).ToList());
    }

    private static EvidenceBlockResponse ToBlockResponse(EvidenceBlock block)
    {
        using var locator = JsonDocument.Parse(block.LocatorJson);
        return new EvidenceBlockResponse(
            block.SourceAssetId,
            block.StableId,
            block.Ordinal,
            block.Content,
            block.LocatorKind,
            locator.RootElement.Clone(),
            block.Revision,
            block.RevisionId,
            block.ApprovalState,
            block.IsExcluded);
    }
}