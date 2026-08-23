using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ContentDependencySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentDependencySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ApprovedReportArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedReportVersion = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedReportHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ApprovedTargetStrategyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentDependencySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentDependencySnapshots_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentEvidenceDependencies",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentEvidenceDependencies", x => new { x.SnapshotId, x.SourceAssetId });
                    table.ForeignKey(
                        name: "FK_ContentEvidenceDependencies_ContentDependencySnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "ContentDependencySnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentDependencySnapshots_ArtifactId",
                table: "ContentDependencySnapshots",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentDependencySnapshots_TenantId_ArtifactId_CreatedAt",
                table: "ContentDependencySnapshots",
                columns: new[] { "TenantId", "ArtifactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentDependencySnapshots_TenantId_CampaignId_ArtifactId_IsCurrent",
                table: "ContentDependencySnapshots",
                columns: new[] { "TenantId", "CampaignId", "ArtifactId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentEvidenceDependencies_TenantId_CampaignId_SourceAssetId_RevisionId",
                table: "ContentEvidenceDependencies",
                columns: new[] { "TenantId", "CampaignId", "SourceAssetId", "RevisionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentEvidenceDependencies");

            migrationBuilder.DropTable(
                name: "ContentDependencySnapshots");
        }
    }
}
