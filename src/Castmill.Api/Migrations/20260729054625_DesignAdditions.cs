using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class DesignAdditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows predate frame jobs, so "clip" is the correct backfill —
            // an empty string would leave them in a mode the worker doesn't know.
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "ClipJobs",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "clip");

            migrationBuilder.CreateTable(
                name: "ArtifactRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactRevisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GenerationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TotalKinds = table.Column<int>(type: "int", nullable: false),
                    ItemsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImageSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TargetWidth = table.Column<int>(type: "int", nullable: false),
                    TargetHeight = table.Column<int>(type: "int", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ModelAlias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceSegmentId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    HeadlineText = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SafeArea = table.Column<bool>(type: "bit", nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PublishedUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BaseImagePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    BaseImageUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageSlots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BrokerPostId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 65000, nullable: false),
                    MediaUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRevisions_TenantId_ArtifactId_Version",
                table: "ArtifactRevisions",
                columns: new[] { "TenantId", "ArtifactId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationRuns_TenantId_CampaignId_StartedAt",
                table: "GenerationRuns",
                columns: new[] { "TenantId", "CampaignId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageSlots_TenantId_CampaignId_Kind",
                table: "ImageSlots",
                columns: new[] { "TenantId", "CampaignId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEntries_TenantId_ScheduledAt",
                table: "ScheduleEntries",
                columns: new[] { "TenantId", "ScheduledAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtifactRevisions");

            migrationBuilder.DropTable(
                name: "GenerationRuns");

            migrationBuilder.DropTable(
                name: "ImageSlots");

            migrationBuilder.DropTable(
                name: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "ClipJobs");
        }
    }
}
