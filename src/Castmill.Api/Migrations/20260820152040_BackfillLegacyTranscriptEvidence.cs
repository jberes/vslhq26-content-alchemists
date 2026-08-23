using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLegacyTranscriptEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE #TranscriptBackfill
                (
                    [ArtifactId] uniqueidentifier NOT NULL,
                    [SourceAssetId] uniqueidentifier NOT NULL,
                    [RevisionId] uniqueidentifier NOT NULL,
                    [TenantId] uniqueidentifier NOT NULL,
                    [CampaignId] uniqueidentifier NOT NULL,
                    [SourceLabel] nvarchar(300) NOT NULL,
                    [SnapshotHash] varchar(64) NOT NULL,
                    [CreatedAt] datetimeoffset NOT NULL,
                    [UpdatedAt] datetimeoffset NOT NULL
                );

                WITH Candidates AS
                (
                    SELECT
                        artifact.[Id] AS [ArtifactId],
                        artifact.[TenantId],
                        artifact.[CampaignId],
                        LEFT(COALESCE(NULLIF(JSON_VALUE(artifact.[ContentJson], '$.source'), ''), artifact.[Title]), 300)
                            AS [SourceLabel],
                        LOWER(CONVERT(varchar(64), HASHBYTES(
                            'SHA2_256',
                            CONVERT(varchar(max), artifact.[ContentJson])
                                COLLATE Latin1_General_100_BIN2_UTF8), 2)) AS [SnapshotHash],
                        artifact.[CreatedAt],
                        artifact.[UpdatedAt],
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY artifact.[TenantId], artifact.[CampaignId], artifact.[ContentJson]
                            ORDER BY artifact.[CreatedAt], artifact.[Id]
                        ) AS [DuplicateRank]
                    FROM [Artifacts] AS artifact
                    WHERE artifact.[Kind] = 'transcript'
                      AND ISJSON(artifact.[ContentJson]) = 1
                      AND JSON_QUERY(artifact.[ContentJson], '$.segments') IS NOT NULL
                      AND EXISTS
                      (
                          SELECT 1
                                                    FROM OPENJSON(artifact.[ContentJson], '$.segments')
                                                    WITH
                                                    (
                                                            [StableId] nvarchar(100) '$.id',
                                                            [Content] nvarchar(max) '$.text'
                                                    ) AS segment
                                                    WHERE NULLIF(segment.[StableId], '') IS NOT NULL
                                                        AND NULLIF(segment.[Content], '') IS NOT NULL
                      )
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM [SourceAssets] AS source
                          WHERE source.[TenantId] = artifact.[TenantId]
                            AND source.[LegacyArtifactId] = artifact.[Id]
                      )
                )
                INSERT INTO #TranscriptBackfill
                SELECT
                    candidate.[ArtifactId],
                    NEWID(),
                    NEWID(),
                    candidate.[TenantId],
                    candidate.[CampaignId],
                    candidate.[SourceLabel],
                    candidate.[SnapshotHash],
                    candidate.[CreatedAt],
                    candidate.[UpdatedAt]
                FROM Candidates AS candidate
                WHERE candidate.[DuplicateRank] = 1
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM [SourceAssets] AS source
                      WHERE source.[TenantId] = candidate.[TenantId]
                        AND source.[CampaignId] = candidate.[CampaignId]
                        AND source.[Kind] = 'transcript'
                        AND source.[SnapshotHash] = candidate.[SnapshotHash]
                  );

                INSERT INTO [SourceAssets]
                (
                    [Id], [TenantId], [CampaignId], [LegacyArtifactId], [Kind], [Modality],
                    [Label], [OriginalUri], [BlobPath], [ContentType], [SizeBytes],
                    [SnapshotIdentity], [SnapshotHash], [CurrentEvidenceRevision],
                    [CurrentEvidenceRevisionId], [ApprovedEvidenceRevision],
                    [ApprovedEvidenceRevisionId], [ApprovedEvidenceHash], [ApprovedAt],
                    [CreatedAt], [UpdatedAt]
                )
                SELECT
                    item.[SourceAssetId], item.[TenantId], item.[CampaignId], item.[ArtifactId],
                    'transcript', 'media', item.[SourceLabel], NULL, NULL, 'application/json',
                    DATALENGTH(artifact.[ContentJson]), 'sha256:' + item.[SnapshotHash],
                    item.[SnapshotHash], 1, item.[RevisionId], 1, item.[RevisionId],
                    item.[SnapshotHash], item.[UpdatedAt], item.[CreatedAt], item.[UpdatedAt]
                FROM #TranscriptBackfill AS item
                INNER JOIN [Artifacts] AS artifact ON artifact.[Id] = item.[ArtifactId];

                INSERT INTO [EvidenceBlocks]
                (
                    [Id], [TenantId], [CampaignId], [SourceAssetId], [StableId], [Ordinal],
                    [Content], [ContentHash], [LocatorKind], [LocatorJson], [Revision],
                    [RevisionId], [ApprovalState], [IsExcluded], [CreatedAt], [UpdatedAt]
                )
                SELECT
                    NEWID(), item.[TenantId], item.[CampaignId], item.[SourceAssetId],
                    LEFT(segment.[StableId], 100),
                    TRY_CONVERT(int, rawSegment.[key]),
                    segment.[Content],
                    LOWER(CONVERT(varchar(64), HASHBYTES(
                        'SHA2_256',
                        CONVERT(varchar(max), segment.[Content])
                            COLLATE Latin1_General_100_BIN2_UTF8), 2)),
                    'media-time-range',
                    (
                        SELECT
                            segment.[StartSeconds] AS [startSeconds],
                            segment.[EndSeconds] AS [endSeconds],
                            segment.[Speaker] AS [speaker],
                            COALESCE(
                                NULLIF(segment.[SourceLabel], ''),
                                item.[SourceLabel]) AS [sourceLabel]
                        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
                    ),
                    1, item.[RevisionId], 'Approved', 0, item.[CreatedAt], item.[UpdatedAt]
                FROM #TranscriptBackfill AS item
                INNER JOIN [Artifacts] AS artifact ON artifact.[Id] = item.[ArtifactId]
                                CROSS APPLY OPENJSON(artifact.[ContentJson], '$.segments') AS rawSegment
                                CROSS APPLY OPENJSON(rawSegment.[value])
                                WITH
                                (
                                        [StableId] nvarchar(100) '$.id',
                                        [Content] nvarchar(max) '$.text',
                                        [StartSeconds] float '$.startSeconds',
                                        [EndSeconds] float '$.endSeconds',
                                        [Speaker] nvarchar(300) '$.speaker',
                                        [SourceLabel] nvarchar(300) '$.sourceLabel'
                                ) AS segment
                                WHERE NULLIF(segment.[StableId], '') IS NOT NULL
                                    AND NULLIF(segment.[Content], '') IS NOT NULL;

                DROP TABLE #TranscriptBackfill;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
