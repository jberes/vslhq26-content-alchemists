using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Endpoints;
using Castmill.Api.Services.Ai;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Evidence;

public interface IContentDependencyService
{
    Task<TranscriptContent?> LoadApprovedTranscriptAsync(
        Guid campaignId, Guid transcriptArtifactId, CancellationToken ct);
    Task<TranscriptContent?> LoadApprovedSourceAsync(
        Guid campaignId, Guid? transcriptArtifactId, CancellationToken ct);
    Task<GenerationEvidenceContext> LoadGenerationEvidenceAsync(
        Guid campaignId, TranscriptContent transcript, CancellationToken ct);
    Task CaptureDeepAnalysisAsync(Artifact report, Campaign campaign, CancellationToken ct);
    Task CaptureStrategyApprovalAsync(Artifact report, Campaign campaign, CancellationToken ct);
    Task CaptureGeneratedAsync(
        Artifact artifact, Campaign campaign, string reason, CancellationToken ct,
        IReadOnlyList<ApprovedEvidenceRevision>? approvedEvidence = null);
    Task<ContentImpactReviewResponse> GetImpactReviewAsync(Guid campaignId, CancellationToken ct);
    Task<ContentImpactItemResponse?> AcknowledgeAsync(
        Guid campaignId, Guid artifactId, CancellationToken ct);
    Task RestoreAsync(
        Artifact artifact, Guid? historicalSnapshotId, CancellationToken ct);
}

public sealed class ContentDependencyService(
    CastmillDbContext db,
    TimeProvider clock) : IContentDependencyService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TranscriptContent?> LoadApprovedTranscriptAsync(
        Guid campaignId, Guid transcriptArtifactId, CancellationToken ct)
    {
        var source = await db.SourceAssets.SingleOrDefaultAsync(
            candidate => candidate.CampaignId == campaignId
                && candidate.LegacyArtifactId == transcriptArtifactId
                && candidate.Kind == SourceKinds.Transcript,
            ct);
        if (source is null)
        {
            var legacyArtifact = await db.Artifacts.SingleOrDefaultAsync(
                artifact => artifact.Id == transcriptArtifactId
                    && artifact.CampaignId == campaignId
                    && artifact.Kind == "transcript",
                ct);
            var legacyTranscript = legacyArtifact is null
                ? null
                : TranscriptService.Parse(legacyArtifact.ContentJson);
            if (legacyArtifact is null || legacyTranscript is null)
            {
                return null;
            }
            source = await BackfillLegacyTranscriptAsync(legacyArtifact, legacyTranscript, ct);
        }
        if (source.ApprovedEvidenceRevision is not { } revision)
        {
            return null;
        }

        var blocks = await db.EvidenceBlocks
            .Where(block => block.SourceAssetId == source.Id
                && block.Revision == revision
                && !block.IsExcluded)
            .OrderBy(block => block.Ordinal)
            .ToListAsync(ct);
        if (blocks.Count == 0)
        {
            return null;
        }

        var segments = blocks.Select(block => ToTranscriptSegment(source, block)).ToList();
        return new TranscriptContent(source.Label, segments, source.Id);
    }

    public async Task<TranscriptContent?> LoadApprovedSourceAsync(
        Guid campaignId, Guid? transcriptArtifactId, CancellationToken ct)
    {
        if (transcriptArtifactId is { } artifactId)
        {
            return await LoadApprovedTranscriptAsync(campaignId, artifactId, ct);
        }

        var evidence = await LoadGenerationEvidenceAsync(
            campaignId, new TranscriptContent("approved evidence", []), ct);
        if (evidence.Blocks.Count == 0)
        {
            return null;
        }
        var segments = evidence.Blocks.Select((block, ordinal) => new TranscriptSegment(
            block.CitationId,
            ordinal,
            ordinal + 1,
            null,
            block.Content,
            block.SourceLabel))
            .ToList();
        return new TranscriptContent("approved evidence", segments);
    }

    public async Task<GenerationEvidenceContext> LoadGenerationEvidenceAsync(
        Guid campaignId, TranscriptContent transcript, CancellationToken ct)
    {
        var rows = await (
            from source in db.SourceAssets
            where source.CampaignId == campaignId
                && source.ApprovedEvidenceRevision != null
                && source.ApprovedEvidenceRevisionId != null
                && source.ApprovedEvidenceHash != null
                && source.ApprovedAt != null
            join matchingBlock in db.EvidenceBlocks.Where(block => !block.IsExcluded)
                on new
                {
                    SourceAssetId = source.Id,
                    Revision = source.ApprovedEvidenceRevision!.Value,
                }
                equals new
                {
                    matchingBlock.SourceAssetId,
                    matchingBlock.Revision,
                }
                into sourceBlocks
            from block in sourceBlocks.DefaultIfEmpty()
            orderby source.CreatedAt, block == null ? 0 : block.Ordinal,
                block == null ? string.Empty : block.StableId
            select new
            {
                SourceId = source.Id,
                source.Label,
                source.CreatedAt,
                Revision = source.ApprovedEvidenceRevision!.Value,
                RevisionId = source.ApprovedEvidenceRevisionId!.Value,
                Hash = source.ApprovedEvidenceHash!,
                ApprovedAt = source.ApprovedAt!.Value,
                Block = block,
            })
            .AsNoTracking()
            .ToListAsync(ct);
        var blocks = rows
            .Where(row => row.Block is not null)
            .Select(row => new GenerationEvidenceBlock(
                row.SourceId,
                row.Label,
                row.Block!.StableId,
                row.Block.Content,
                row.Block.LocatorKind,
                row.Block.LocatorJson))
            .ToList();

        if (rows.Count == 0)
        {
            return GenerationEvidenceContext.FromTranscript(transcript);
        }

        var approvals = rows
            .GroupBy(row => row.SourceId)
            .Select(group => group.First())
            .Select(source => new ApprovedEvidenceRevision(
                source.SourceId,
                source.Revision,
                source.RevisionId,
                source.Hash,
                source.ApprovedAt))
            .ToList();
        return new GenerationEvidenceContext(
            transcript,
            blocks,
            approvals,
            transcript.SourceAssetId);
    }

    public Task CaptureDeepAnalysisAsync(
        Artifact report, Campaign campaign, CancellationToken ct) =>
        CaptureAsync(report, campaign, ContentDependencyReasons.DeepAnalysis, null, ct);

    public async Task CaptureStrategyApprovalAsync(
        Artifact report, Campaign campaign, CancellationToken ct)
    {
        var strategy = BuildStrategyIdentity(report, campaign);
        await CaptureAsync(
            report, campaign, ContentDependencyReasons.StrategyApproved, strategy, ct);
    }

    public async Task CaptureGeneratedAsync(
        Artifact artifact, Campaign campaign, string reason, CancellationToken ct,
        IReadOnlyList<ApprovedEvidenceRevision>? approvedEvidence = null)
    {
        var strategy = await GetApprovedStrategyAsync(campaign, ct);
        await CaptureAsync(artifact, campaign, reason, strategy, ct, approvedEvidence);
    }

    public async Task<ContentImpactReviewResponse> GetImpactReviewAsync(
        Guid campaignId, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(campaign => campaign.Id == campaignId, ct))
        {
            return new ContentImpactReviewResponse(campaignId, []);
        }

        var campaign = await db.Campaigns.SingleAsync(candidate => candidate.Id == campaignId, ct);
        var current = await GetCurrentIdentityAsync(campaign, ct);
        var artifacts = await db.Artifacts
            .Where(artifact => artifact.CampaignId == campaignId)
            .OrderBy(artifact => artifact.CreatedAt)
            .ToListAsync(ct);
        var userContent = artifacts.Where(artifact => ArtifactKinds.IsUserContent(artifact.Kind)).ToList();
        var artifactIds = userContent.Select(artifact => artifact.Id).ToList();
        var snapshots = await db.ContentDependencySnapshots
            .Where(snapshot => artifactIds.Contains(snapshot.ArtifactId) && snapshot.IsCurrent)
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ToListAsync(ct);
        var snapshotIds = snapshots.Select(snapshot => snapshot.Id).ToList();
        var markers = await db.ContentEvidenceDependencies
            .Where(marker => snapshotIds.Contains(marker.SnapshotId))
            .OrderBy(marker => marker.SourceAssetId)
            .ToListAsync(ct);
        var latest = snapshots
            .GroupBy(snapshot => snapshot.ArtifactId)
            .ToDictionary(group => group.Key, group => group.First());

        var impacts = userContent.Select(artifact =>
        {
            latest.TryGetValue(artifact.Id, out var snapshot);
            var prior = snapshot is null
                ? null
                : ToIdentity(snapshot, markers.Where(marker => marker.SnapshotId == snapshot.Id));
            var state = Classify(prior, current);
            var reasons = BuildReasons(state, prior, current);
            var supported = artifact.Kind == "blog" || Generators.Find(artifact.Kind) is not null;
            var hasCurrentInputs = current.Evidence.Count > 0
                && current.ReportHash is not null
                && current.TargetStrategyHash is not null;
            return new ContentImpactItemResponse(
                artifact.Id,
                artifact.Kind,
                artifact.Title,
                state,
                reasons,
                prior,
                current,
                hasCurrentInputs,
                supported && hasCurrentInputs,
                supported
                    ? hasCurrentInputs ? null : "Approve evidence and an SEO/AEO strategy before regenerating."
                    : $"{artifact.Kind} has no registered generator path.");
        }).ToList();

        return new ContentImpactReviewResponse(campaignId, impacts);
    }

    public async Task<ContentImpactItemResponse?> AcknowledgeAsync(
        Guid campaignId, Guid artifactId, CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(
            candidate => candidate.Id == campaignId, ct);
        var artifact = await db.Artifacts.SingleOrDefaultAsync(
            candidate => candidate.Id == artifactId
                && candidate.CampaignId == campaignId, ct);
        if (campaign is null || artifact is null || !ArtifactKinds.IsUserContent(artifact.Kind))
        {
            return null;
        }

        var current = await GetCurrentIdentityAsync(campaign, ct);
        if (current.Evidence.Count == 0
            || current.ReportHash is null
            || current.TargetStrategyHash is null)
        {
            return null;
        }

        await CaptureAsync(
            artifact,
            campaign,
            ContentDependencyReasons.Acknowledged,
            new StrategyIdentity(
                current.ReportArtifactId!.Value,
                current.ReportVersion!.Value,
                current.ReportHash,
                current.TargetStrategyHash),
            ct);
        var review = await GetImpactReviewAsync(campaignId, ct);
        return review.Artifacts.Single(item => item.ArtifactId == artifactId);
    }

    public async Task RestoreAsync(
        Artifact artifact, Guid? historicalSnapshotId, CancellationToken ct)
    {
        var historical = historicalSnapshotId is null
            ? null
            : await db.ContentDependencySnapshots.SingleOrDefaultAsync(
                snapshot => snapshot.Id == historicalSnapshotId
                    && snapshot.ArtifactId == artifact.Id
                    && snapshot.CampaignId == artifact.CampaignId,
                ct);
        var historicalMarkers = historical is null
            ? []
            : await db.ContentEvidenceDependencies
                .Where(marker => marker.SnapshotId == historical.Id)
                .ToListAsync(ct);
        var current = await db.ContentDependencySnapshots
            .Where(snapshot => snapshot.ArtifactId == artifact.Id && snapshot.IsCurrent)
            .ToListAsync(ct);
        foreach (var snapshot in current)
        {
            snapshot.IsCurrent = false;
        }

        var restoredId = Guid.NewGuid();
        db.ContentDependencySnapshots.Add(new ContentDependencySnapshot
        {
            Id = restoredId,
            TenantId = artifact.TenantId,
            CampaignId = artifact.CampaignId,
            ArtifactId = artifact.Id,
            IsCurrent = true,
            Reason = ContentDependencyReasons.Restored,
            ApprovedReportArtifactId = historical?.ApprovedReportArtifactId,
            ApprovedReportVersion = historical?.ApprovedReportVersion,
            ApprovedReportHash = historical?.ApprovedReportHash,
            ApprovedTargetStrategyHash = historical?.ApprovedTargetStrategyHash,
            CreatedAt = clock.GetUtcNow(),
        });
        db.ContentEvidenceDependencies.AddRange(historicalMarkers.Select(marker =>
            new ContentEvidenceDependency
            {
                TenantId = artifact.TenantId,
                CampaignId = artifact.CampaignId,
                SnapshotId = restoredId,
                SourceAssetId = marker.SourceAssetId,
                Revision = marker.Revision,
                RevisionId = marker.RevisionId,
                Hash = marker.Hash,
                ApprovedAt = marker.ApprovedAt,
            }));
        await db.SaveChangesAsync(ct);
    }

    private async Task CaptureAsync(
        Artifact artifact,
        Campaign campaign,
        string reason,
        StrategyIdentity? strategy,
        CancellationToken ct,
        IReadOnlyList<ApprovedEvidenceRevision>? approvedEvidence = null)
    {
        var markers = approvedEvidence ?? await GetApprovedEvidenceAsync(campaign.Id, ct);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await db.ContentDependencySnapshots
                .Where(snapshot => snapshot.ArtifactId == artifact.Id && snapshot.IsCurrent)
                .ToListAsync(ct);
            foreach (var snapshot in current)
            {
                snapshot.IsCurrent = false;
            }

            var id = Guid.NewGuid();
            db.ContentDependencySnapshots.Add(new ContentDependencySnapshot
            {
                Id = id,
                TenantId = artifact.TenantId,
                CampaignId = campaign.Id,
                ArtifactId = artifact.Id,
                IsCurrent = true,
                Reason = reason,
                ApprovedReportArtifactId = strategy?.ReportArtifactId,
                ApprovedReportVersion = strategy?.ReportVersion,
                ApprovedReportHash = strategy?.ReportHash,
                ApprovedTargetStrategyHash = strategy?.TargetStrategyHash,
                CreatedAt = clock.GetUtcNow(),
            });
            db.ContentEvidenceDependencies.AddRange(markers.Select(marker =>
                new ContentEvidenceDependency
                {
                    TenantId = artifact.TenantId,
                    CampaignId = campaign.Id,
                    SnapshotId = id,
                    SourceAssetId = marker.SourceAssetId,
                    Revision = marker.Revision,
                    RevisionId = marker.RevisionId,
                    Hash = marker.Hash,
                    ApprovedAt = marker.ApprovedAt,
                }));
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
        throw new InvalidOperationException("Dependency snapshot retry limit was exhausted.");
    }

    private async Task<SourceAsset> BackfillLegacyTranscriptAsync(
        Artifact artifact,
        TranscriptContent transcript,
        CancellationToken ct)
    {
        var snapshotHash = EvidenceRevisionHasher.HashContent(artifact.ContentJson);
        var existing = await db.SourceAssets.SingleOrDefaultAsync(
            source => source.CampaignId == artifact.CampaignId
                && source.Kind == SourceKinds.Transcript
                && source.LegacyArtifactId == artifact.Id,
            ct);
        existing ??= await db.SourceAssets
            .Where(source => source.CampaignId == artifact.CampaignId
                && source.Kind == SourceKinds.Transcript
                && source.SnapshotHash == snapshotHash)
            .OrderBy(source => source.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var now = clock.GetUtcNow();
        var revisionId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var usedStableIds = new HashSet<string>(StringComparer.Ordinal);
        var blocks = transcript.Segments.Select((segment, ordinal) =>
        {
            var sourceLabel = string.IsNullOrWhiteSpace(segment.SourceLabel)
                ? transcript.Source
                : segment.SourceLabel;
            return new EvidenceBlock
            {
                Id = Guid.NewGuid(),
                TenantId = artifact.TenantId,
                CampaignId = artifact.CampaignId,
                SourceAssetId = sourceId,
                StableId = LegacyEvidenceBackfillService.UniqueStableId(
                    segment.Id, ordinal, usedStableIds),
                Ordinal = ordinal,
                Content = segment.Text,
                ContentHash = EvidenceRevisionHasher.HashContent(segment.Text),
                LocatorKind = EvidenceLocatorKinds.MediaTimeRange,
                LocatorJson = JsonSerializer.Serialize(
                    new
                    {
                        segment.StartSeconds,
                        segment.EndSeconds,
                        segment.Speaker,
                        SourceLabel = sourceLabel,
                    },
                    Json),
                Revision = 1,
                RevisionId = revisionId,
                ApprovalState = EvidenceApprovalStates.Approved,
                IsExcluded = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }).ToList();
        var source = new SourceAsset
        {
            Id = sourceId,
            TenantId = artifact.TenantId,
            CampaignId = artifact.CampaignId,
            LegacyArtifactId = artifact.Id,
            Kind = SourceKinds.Transcript,
            Modality = SourceModalities.Media,
            Label = LegacyEvidenceBackfillService.NormalizeLabel(transcript.Source),
            SnapshotIdentity = $"sha256:{snapshotHash}",
            SnapshotHash = snapshotHash,
            CurrentEvidenceRevision = 1,
            CurrentEvidenceRevisionId = revisionId,
            ApprovedEvidenceRevision = 1,
            ApprovedEvidenceRevisionId = revisionId,
            ApprovedEvidenceHash = EvidenceRevisionHasher.HashApproved(blocks),
            ApprovedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SourceAssets.Add(source);
        db.EvidenceBlocks.AddRange(blocks);
        try
        {
            await db.SaveChangesAsync(ct);
            return source;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.SourceAssets.SingleOrDefaultAsync(
                candidate => candidate.CampaignId == artifact.CampaignId
                    && candidate.Kind == SourceKinds.Transcript
                    && (candidate.LegacyArtifactId == artifact.Id
                        || candidate.SnapshotHash == snapshotHash),
                ct);
            if (winner is not null)
            {
                return winner;
            }
            throw;
        }
    }

    private async Task<ContentDependencyIdentity> GetCurrentIdentityAsync(
        Campaign campaign, CancellationToken ct)
    {
        var evidence = await GetApprovedEvidenceAsync(campaign.Id, ct);
        var strategy = await GetApprovedStrategyAsync(campaign, ct);
        return new ContentDependencyIdentity(
            evidence,
            strategy?.ReportArtifactId,
            strategy?.ReportVersion,
            strategy?.ReportHash,
            strategy?.TargetStrategyHash);
    }

    private async Task<IReadOnlyList<ApprovedEvidenceRevision>> GetApprovedEvidenceAsync(
        Guid campaignId, CancellationToken ct) =>
        await db.SourceAssets
            .Where(source => source.CampaignId == campaignId
                && source.ApprovedEvidenceRevision != null
                && source.ApprovedEvidenceRevisionId != null
                && source.ApprovedEvidenceHash != null
                && source.ApprovedAt != null)
            .OrderBy(source => source.Id)
            .Select(source => new ApprovedEvidenceRevision(
                source.Id,
                source.ApprovedEvidenceRevision!.Value,
                source.ApprovedEvidenceRevisionId!.Value,
                source.ApprovedEvidenceHash!,
                source.ApprovedAt!.Value))
            .ToListAsync(ct);

    private async Task<StrategyIdentity?> GetApprovedStrategyAsync(
        Campaign campaign, CancellationToken ct)
    {
        var targetsHash = HashTargets(campaign.SeoTargetsJson);
        if (targetsHash is null)
        {
            return null;
        }

        var report = await db.Artifacts
            .Where(artifact => artifact.CampaignId == campaign.Id && artifact.Kind == "seo-report")
            .OrderByDescending(artifact => artifact.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (report is not null && IsApprovedReport(report.ContentJson))
        {
            return BuildStrategyIdentity(report, campaign);
        }

        var approved = await db.ContentDependencySnapshots
            .Where(snapshot => snapshot.CampaignId == campaign.Id
                && snapshot.Reason == ContentDependencyReasons.StrategyApproved
                && snapshot.ApprovedReportArtifactId != null
                && snapshot.ApprovedReportVersion != null
                && snapshot.ApprovedReportHash != null
                && snapshot.ApprovedTargetStrategyHash != null)
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return approved is null
            ? null
            : new StrategyIdentity(
                approved.ApprovedReportArtifactId!.Value,
                approved.ApprovedReportVersion!.Value,
                approved.ApprovedReportHash!,
                approved.ApprovedTargetStrategyHash!);
    }

    private static StrategyIdentity BuildStrategyIdentity(Artifact report, Campaign campaign) =>
        new(report.Id, report.Version, HashReportStrategy(report.ContentJson),
            HashTargets(campaign.SeoTargetsJson)
                ?? throw new InvalidOperationException("An approved strategy requires campaign targets."));

    internal static string Classify(
        ContentDependencyIdentity? prior,
        ContentDependencyIdentity current)
    {
        if (prior is null
            || prior.Evidence.Count == 0
            || prior.ReportHash is null
            || prior.TargetStrategyHash is null)
        {
            return ContentStalenessStates.Unknown;
        }

        var evidenceChanged = !MarkersEqual(prior.Evidence, current.Evidence);
        var strategyChanged = !string.Equals(prior.ReportHash, current.ReportHash, StringComparison.Ordinal)
            || !string.Equals(prior.TargetStrategyHash, current.TargetStrategyHash, StringComparison.Ordinal);
        return (evidenceChanged, strategyChanged) switch
        {
            (true, true) => ContentStalenessStates.BothChanged,
            (true, false) => ContentStalenessStates.EvidenceChanged,
            (false, true) => ContentStalenessStates.StrategyChanged,
            _ => ContentStalenessStates.Fresh,
        };
    }

    internal static string HashReportStrategy(string contentJson)
    {
        var report = JsonSerializer.Deserialize<SeoAnalysisReportResponse>(contentJson, Json)
            ?? throw new JsonException("The SEO/AEO report could not be read.");
        return Hash(JsonSerializer.Serialize(new
        {
            report.Research,
            report.Serp,
            report.Recommendations,
            report.SiteUrl,
            report.CampaignBrief,
            report.Insights,
        }, Json));
    }

    internal static string? HashTargets(string? targetsJson)
    {
        var targets = CampaignEndpoints.ParseSeoTargets(targetsJson);
        return targets.Keywords.Count == 0 && targets.Questions.Count == 0
            ? null
            : Hash(JsonSerializer.Serialize(targets, Json));
    }

    private static bool IsApprovedReport(string contentJson)
    {
        try
        {
            return JsonSerializer.Deserialize<SeoAnalysisReportResponse>(contentJson, Json)?.Status
                == "Approved";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ContentDependencyIdentity ToIdentity(
        ContentDependencySnapshot snapshot,
        IEnumerable<ContentEvidenceDependency> markers) =>
        new(
            markers.OrderBy(marker => marker.SourceAssetId)
                .Select(marker => new ApprovedEvidenceRevision(
                    marker.SourceAssetId,
                    marker.Revision,
                    marker.RevisionId,
                    marker.Hash,
                    marker.ApprovedAt))
                .ToList(),
            snapshot.ApprovedReportArtifactId,
            snapshot.ApprovedReportVersion,
            snapshot.ApprovedReportHash,
            snapshot.ApprovedTargetStrategyHash);

    private static IReadOnlyList<ContentImpactReason> BuildReasons(
        string state,
        ContentDependencyIdentity? prior,
        ContentDependencyIdentity current) => state switch
    {
        ContentStalenessStates.EvidenceChanged =>
            [new("evidence", "The approved evidence revision set has changed since this content was generated.")],
        ContentStalenessStates.StrategyChanged =>
            [new("strategy", "The approved SEO/AEO report or target strategy has changed since this content was generated.")],
        ContentStalenessStates.BothChanged =>
            [new("evidence", "The approved evidence revision set has changed since this content was generated."),
             new("strategy", "The approved SEO/AEO report or target strategy has changed since this content was generated.")],
        ContentStalenessStates.Unknown =>
            [new("transition", prior is null
                ? "This artifact predates dependency tracking and needs transition review."
                : "This artifact does not have a complete approved evidence and strategy snapshot.")],
        _ => [],
    };

    private static bool MarkersEqual(
        IReadOnlyList<ApprovedEvidenceRevision> left,
        IReadOnlyList<ApprovedEvidenceRevision> right) =>
        left.OrderBy(marker => marker.SourceAssetId)
            .SequenceEqual(right.OrderBy(marker => marker.SourceAssetId));

    private static TranscriptSegment ToTranscriptSegment(
        SourceAsset source,
        EvidenceBlock block)
    {
        using var locator = JsonDocument.Parse(block.LocatorJson);
        var root = locator.RootElement;
        var start = root.TryGetProperty("startSeconds", out var startNode)
            ? startNode.GetDouble()
            : block.Ordinal;
        var end = root.TryGetProperty("endSeconds", out var endNode)
            ? endNode.GetDouble()
            : start;
        var speaker = root.TryGetProperty("speaker", out var speakerNode)
            && speakerNode.ValueKind == JsonValueKind.String
                ? speakerNode.GetString()
                : null;
        var sourceLabel = root.TryGetProperty("sourceLabel", out var sourceNode)
            && sourceNode.ValueKind == JsonValueKind.String
                ? sourceNode.GetString()
                : source.Label;
        return new TranscriptSegment(
            block.StableId, start, end, speaker, block.Content, sourceLabel);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record StrategyIdentity(
        Guid ReportArtifactId,
        long ReportVersion,
        string ReportHash,
        string TargetStrategyHash);
}