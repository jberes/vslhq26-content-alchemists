using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class PersistCampaignRunPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Intent",
                table: "Campaigns",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputRecipeJson",
                table: "Campaigns",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Intent",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "OutputRecipeJson",
                table: "Campaigns");
        }
    }
}
