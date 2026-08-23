using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ResumableMediaUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    NextBlockIndex = table.Column<int>(type: "int", nullable: false),
                    BlockIdsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 80000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TranscriptArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaUploads", x => x.Id);
                    table.CheckConstraint("CK_MediaUploads_Progress", "[UploadedBytes] >= 0 AND [NextBlockIndex] >= 0");
                    table.CheckConstraint("CK_MediaUploads_Status", "[Status] IN ('Uploading', 'Committed', 'Transcribing', 'Completed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_MediaUploads_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaUploads_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaUploads_AssetId",
                table: "MediaUploads",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaUploads_CampaignId",
                table: "MediaUploads",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaUploads_TenantId_CampaignId_UpdatedAt",
                table: "MediaUploads",
                columns: new[] { "TenantId", "CampaignId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaUploads");
        }
    }
}
