using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ContentItemImageReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PromptMode",
                table: "ImageSlots",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<string>(
                name: "ReferenceAssetIdsJson",
                table: "ImageSlots",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromptMode",
                table: "ImageSlots");

            migrationBuilder.DropColumn(
                name: "ReferenceAssetIdsJson",
                table: "ImageSlots");
        }
    }
}
