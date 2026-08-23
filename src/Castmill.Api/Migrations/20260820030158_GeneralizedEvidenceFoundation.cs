using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizedEvidenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegacyArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Modality = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    OriginalUri = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BlobPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    SnapshotIdentity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SnapshotHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CurrentEvidenceRevision = table.Column<int>(type: "int", nullable: false),
                    CurrentEvidenceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedEvidenceRevision = table.Column<int>(type: "int", nullable: true),
                    ApprovedEvidenceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedEvidenceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceAssets", x => x.Id);
                    table.CheckConstraint("CK_SourceAssets_EvidenceRevision", "[CurrentEvidenceRevision] >= 1 AND (([ApprovedEvidenceRevision] IS NULL AND [ApprovedEvidenceRevisionId] IS NULL AND [ApprovedEvidenceHash] IS NULL AND [ApprovedAt] IS NULL) OR ([ApprovedEvidenceRevision] IS NOT NULL AND [ApprovedEvidenceRevisionId] IS NOT NULL AND [ApprovedEvidenceHash] IS NOT NULL AND [ApprovedAt] IS NOT NULL AND [ApprovedEvidenceRevision] <= [CurrentEvidenceRevision]))");
                    table.CheckConstraint("CK_SourceAssets_SizeBytes", "[SizeBytes] IS NULL OR [SizeBytes] >= 0");
                    table.ForeignKey(
                        name: "FK_SourceAssets_Artifacts_LegacyArtifactId",
                        column: x => x.LegacyArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SourceAssets_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StableId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LocatorKind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LocatorJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsExcluded = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceBlocks", x => x.Id);
                    table.CheckConstraint("CK_EvidenceBlocks_ApprovalState", "[ApprovalState] IN ('Draft', 'Approved')");
                    table.CheckConstraint("CK_EvidenceBlocks_Ordinal", "[Ordinal] >= 0");
                    table.CheckConstraint("CK_EvidenceBlocks_Revision", "[Revision] >= 1");
                    table.ForeignKey(
                        name: "FK_EvidenceBlocks_SourceAssets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "SourceAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceBlocks_SourceAssetId",
                table: "EvidenceBlocks",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceBlocks_TenantId_CampaignId_SourceAssetId_Revision_Ordinal",
                table: "EvidenceBlocks",
                columns: new[] { "TenantId", "CampaignId", "SourceAssetId", "Revision", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceBlocks_TenantId_SourceAssetId_Revision_StableId",
                table: "EvidenceBlocks",
                columns: new[] { "TenantId", "SourceAssetId", "Revision", "StableId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_CampaignId",
                table: "SourceAssets",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_LegacyArtifactId",
                table: "SourceAssets",
                column: "LegacyArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_TenantId_CampaignId_Kind",
                table: "SourceAssets",
                columns: new[] { "TenantId", "CampaignId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_TenantId_CampaignId_Kind_SnapshotHash",
                table: "SourceAssets",
                columns: new[] { "TenantId", "CampaignId", "Kind", "SnapshotHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_TenantId_LegacyArtifactId",
                table: "SourceAssets",
                columns: new[] { "TenantId", "LegacyArtifactId" },
                unique: true,
                filter: "[LegacyArtifactId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvidenceBlocks");

            migrationBuilder.DropTable(
                name: "SourceAssets");
        }
    }
}
