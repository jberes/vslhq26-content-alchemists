using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Evidence;

public interface ILegacyEvidenceBackfillService
{
    Task<int> BackfillAsync(CancellationToken ct);
}

public sealed class LegacyEvidenceBackfillService(
    CastmillDbContext db,
    TimeProvider clock) : ILegacyEvidenceBackfillService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> BackfillAsync(CancellationToken ct)
    {
        var added = 0;
        const int batchSize = 50;
        for (var offset = 0; ; offset += batchSize)
        {
            var artifacts = await db.Artifacts
                .IgnoreQueryFilters()
                .Where(artifact => artifact.Kind == "transcript")
                .OrderBy(artifact => artifact.CreatedAt)
                .ThenBy(artifact => artifact.Id)
                .Skip(offset)
                .Take(batchSize)
                .AsNoTracking()
                .ToListAsync(ct);
            if (artifacts.Count == 0)
            {
                return added;
            }

            foreach (var artifact in artifacts)
            {
                if (await db.SourceAssets.IgnoreQueryFilters().AnyAsync(
                    source => source.LegacyArtifactId == artifact.Id, ct))
                {
                    continue;
                }
                var transcript = TranscriptService.Parse(artifact.ContentJson);
                if (transcript is null)
                {
                    continue;
                }
                var snapshotHash = EvidenceRevisionHasher.HashContent(artifact.ContentJson);
                if (await db.SourceAssets.IgnoreQueryFilters().AnyAsync(source =>
                    source.TenantId == artifact.TenantId
                    && source.CampaignId == artifact.CampaignId
                    && source.Kind == SourceKinds.Transcript
                    && source.SnapshotHash == snapshotHash, ct))
                {
                    continue;
                }

                var now = clock.GetUtcNow();
                var sourceId = Guid.NewGuid();
                var revisionId = Guid.NewGuid();
                var usedStableIds = new HashSet<string>(StringComparer.Ordinal);
                var blocks = transcript.Segments
                    .Select((segment, ordinal) => new EvidenceBlock
                    {
                        Id = Guid.NewGuid(),
                        TenantId = artifact.TenantId,
                        CampaignId = artifact.CampaignId,
                        SourceAssetId = sourceId,
                        StableId = UniqueStableId(segment.Id, ordinal, usedStableIds),
                        Ordinal = ordinal,
                        Content = segment.Text,
                        ContentHash = EvidenceRevisionHasher.HashContent(segment.Text),
                        LocatorKind = EvidenceLocatorKinds.MediaTimeRange,
                        LocatorJson = JsonSerializer.Serialize(new
                        {
                            segment.StartSeconds,
                            segment.EndSeconds,
                            segment.Speaker,
                            SourceLabel = string.IsNullOrWhiteSpace(segment.SourceLabel)
                                ? transcript.Source
                                : segment.SourceLabel,
                        }, Json),
                        Revision = 1,
                        RevisionId = revisionId,
                        ApprovalState = EvidenceApprovalStates.Approved,
                        IsExcluded = false,
                        CreatedAt = now,
                        UpdatedAt = now,
                    })
                    .ToList();
                if (blocks.Count == 0)
                {
                    continue;
                }

                db.SourceAssets.Add(new SourceAsset
                {
                    Id = sourceId,
                    TenantId = artifact.TenantId,
                    CampaignId = artifact.CampaignId,
                    LegacyArtifactId = artifact.Id,
                    Kind = SourceKinds.Transcript,
                    Modality = SourceModalities.Media,
                    Label = NormalizeLabel(transcript.Source),
                    ContentType = "application/json",
                    SizeBytes = System.Text.Encoding.UTF8.GetByteCount(artifact.ContentJson),
                    SnapshotIdentity = $"sha256:{snapshotHash}",
                    SnapshotHash = snapshotHash,
                    CurrentEvidenceRevision = 1,
                    CurrentEvidenceRevisionId = revisionId,
                    ApprovedEvidenceRevision = 1,
                    ApprovedEvidenceRevisionId = revisionId,
                    ApprovedEvidenceHash = EvidenceRevisionHasher.HashApproved(blocks),
                    ApprovedAt = now,
                    CreatedAt = artifact.CreatedAt,
                    UpdatedAt = now,
                });
                db.EvidenceBlocks.AddRange(blocks);
                try
                {
                    await db.SaveChangesAsync(ct);
                    added++;
                    db.ChangeTracker.Clear();
                }
                catch (DbUpdateException ex) when (IsUniqueConflict(ex))
                {
                    db.ChangeTracker.Clear();
                }
            }
        }
    }

    private static bool IsUniqueConflict(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.SqlClient.SqlException
        {
            Number: 2601 or 2627,
        };

    internal static string UniqueStableId(
        string value, int ordinal, HashSet<string> usedStableIds)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            normalized = $"legacy-{ordinal + 1:D4}";
        }
        normalized = normalized.Length <= 100 ? normalized : normalized[..100];
        if (usedStableIds.Add(normalized))
        {
            return normalized;
        }

        for (var duplicate = 2; ; duplicate++)
        {
            var suffix = $"-{duplicate}";
            var prefixLength = Math.Min(normalized.Length, 100 - suffix.Length);
            var candidate = normalized[..prefixLength] + suffix;
            if (usedStableIds.Add(candidate))
            {
                return candidate;
            }
        }
    }

    internal static string NormalizeLabel(string value)
    {
        var normalized = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length == 0)
        {
            return "Legacy transcript";
        }
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }
}

public sealed class LegacyEvidenceBackfillWorker(
    IServiceScopeFactory scopes,
    ILogger<LegacyEvidenceBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= 8 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var backfill = scope.ServiceProvider
                    .GetRequiredService<ILegacyEvidenceBackfillService>();
                var added = await backfill.BackfillAsync(stoppingToken);
                if (added > 0 && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Backfilled generalized evidence for {Count} legacy transcripts.", added);
                }
                return;
            }
            catch (Exception ex) when (attempt < 8
                && ex is Microsoft.Data.SqlClient.SqlException
                    or DbUpdateException
                    or InvalidOperationException)
            {
                logger.LogWarning(
                    "Legacy evidence backfill attempt {Attempt} could not run yet: {Message}",
                    attempt,
                    ex.Message.Split('\n')[0]);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)), stoppingToken);
            }
        }
    }
}
