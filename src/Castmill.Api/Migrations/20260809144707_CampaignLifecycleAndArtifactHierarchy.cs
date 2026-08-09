using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class CampaignLifecycleAndArtifactHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Campaigns",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Campaigns",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentArtifactId",
                table: "Artifacts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_TenantId_ParentArtifactId",
                table: "Artifacts",
                columns: new[] { "TenantId", "ParentArtifactId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Artifacts_TenantId_ParentArtifactId",
                table: "Artifacts");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "ParentArtifactId",
                table: "Artifacts");
        }
    }
}
