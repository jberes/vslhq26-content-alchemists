using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ImageEditorMediaAndBrandKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "SourceAssets",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalPath",
                table: "SourceAssets",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MediaAssetId",
                table: "SourceAssets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverlaySpecJson",
                table: "ImageSlots",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TechnicalBriefJson",
                table: "Artifacts",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BrandKnowledgeSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    QueryPath = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    QueryField = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TokenCiphertext = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandKnowledgeSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrandMcpServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AuthorizationCiphertext = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AllowedToolsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandMcpServers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrandSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AppliesTo = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSkills", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandKnowledgeSources_TenantId_BrandId",
                table: "BrandKnowledgeSources",
                columns: new[] { "TenantId", "BrandId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandMcpServers_TenantId_BrandId",
                table: "BrandMcpServers",
                columns: new[] { "TenantId", "BrandId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSkills_TenantId_BrandId",
                table: "BrandSkills",
                columns: new[] { "TenantId", "BrandId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandKnowledgeSources");

            migrationBuilder.DropTable(
                name: "BrandMcpServers");

            migrationBuilder.DropTable(
                name: "BrandSkills");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "SourceAssets");

            migrationBuilder.DropColumn(
                name: "LocalPath",
                table: "SourceAssets");

            migrationBuilder.DropColumn(
                name: "MediaAssetId",
                table: "SourceAssets");

            migrationBuilder.DropColumn(
                name: "OverlaySpecJson",
                table: "ImageSlots");

            migrationBuilder.DropColumn(
                name: "TechnicalBriefJson",
                table: "Artifacts");
        }
    }
}
