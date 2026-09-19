using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class VideoReferenceImagesV11 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReferenceCropPresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SourceWidth = table.Column<int>(type: "int", nullable: false),
                    SourceHeight = table.Column<int>(type: "int", nullable: false),
                    CropX = table.Column<int>(type: "int", nullable: false),
                    CropY = table.Column<int>(type: "int", nullable: false),
                    CropWidth = table.Column<int>(type: "int", nullable: false),
                    CropHeight = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceCropPresets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VideoAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SelectionGoal = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    SourceWidth = table.Column<int>(type: "int", nullable: false),
                    SourceHeight = table.Column<int>(type: "int", nullable: false),
                    FrameRate = table.Column<double>(type: "float", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferenceSets_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VideoAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFrameAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DerivedAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceTimestampMs = table.Column<long>(type: "bigint", nullable: false),
                    SourceFrameNumber = table.Column<long>(type: "bigint", nullable: true),
                    CropX = table.Column<int>(type: "int", nullable: false),
                    CropY = table.Column<int>(type: "int", nullable: false),
                    CropWidth = table.Column<int>(type: "int", nullable: false),
                    CropHeight = table.Column<int>(type: "int", nullable: false),
                    CropMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SelectionMethod = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PerceptualHash = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    QualityScore = table.Column<double>(type: "float", nullable: true),
                    AiSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferenceImages_ReferenceSets_ReferenceSetId",
                        column: x => x.ReferenceSetId,
                        principalTable: "ReferenceSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceCropPresets_TenantId_BrandId_Name",
                table: "ReferenceCropPresets",
                columns: new[] { "TenantId", "BrandId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceImages_ReferenceSetId",
                table: "ReferenceImages",
                column: "ReferenceSetId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceImages_TenantId_CampaignId_VideoAssetId",
                table: "ReferenceImages",
                columns: new[] { "TenantId", "CampaignId", "VideoAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceImages_TenantId_ReferenceSetId_SortOrder",
                table: "ReferenceImages",
                columns: new[] { "TenantId", "ReferenceSetId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceSets_CampaignId",
                table: "ReferenceSets",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceSets_TenantId_CampaignId_VideoAssetId",
                table: "ReferenceSets",
                columns: new[] { "TenantId", "CampaignId", "VideoAssetId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferenceCropPresets");

            migrationBuilder.DropTable(
                name: "ReferenceImages");

            migrationBuilder.DropTable(
                name: "ReferenceSets");
        }
    }
}
