using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactScopedImageSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageSlots_TenantId_CampaignId_Kind",
                table: "ImageSlots");

            migrationBuilder.AddColumn<Guid>(
                name: "ArtifactId",
                table: "ImageSlots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageSlots_Tenant_Campaign_Artifact_Kind",
                table: "ImageSlots",
                columns: new[] { "TenantId", "CampaignId", "ArtifactId", "Kind" },
                unique: true,
                filter: "[ArtifactId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ImageSlots_Tenant_Campaign_Kind_NoArtifact",
                table: "ImageSlots",
                columns: new[] { "TenantId", "CampaignId", "Kind" },
                unique: true,
                filter: "[ArtifactId] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageSlots_Tenant_Campaign_Artifact_Kind",
                table: "ImageSlots");

            migrationBuilder.DropIndex(
                name: "IX_ImageSlots_Tenant_Campaign_Kind_NoArtifact",
                table: "ImageSlots");

            migrationBuilder.DropColumn(
                name: "ArtifactId",
                table: "ImageSlots");

            migrationBuilder.CreateIndex(
                name: "IX_ImageSlots_TenantId_CampaignId_Kind",
                table: "ImageSlots",
                columns: new[] { "TenantId", "CampaignId", "Kind" },
                unique: true);
        }
    }
}
