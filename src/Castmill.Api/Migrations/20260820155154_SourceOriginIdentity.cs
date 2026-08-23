using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class SourceOriginIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceAssets_TenantId_CampaignId_Kind_SnapshotHash",
                table: "SourceAssets");

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_TenantId_CampaignId_Kind_SnapshotIdentity",
                table: "SourceAssets",
                columns: new[] { "TenantId", "CampaignId", "Kind", "SnapshotIdentity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceAssets_TenantId_CampaignId_Kind_SnapshotIdentity",
                table: "SourceAssets");

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_TenantId_CampaignId_Kind_SnapshotHash",
                table: "SourceAssets",
                columns: new[] { "TenantId", "CampaignId", "Kind", "SnapshotHash" },
                unique: true);
        }
    }
}
