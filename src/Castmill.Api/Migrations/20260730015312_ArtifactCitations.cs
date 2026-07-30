using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactCitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CitationsJson",
                table: "Artifacts",
                type: "nvarchar(max)",
                nullable: true,
                computedColumnSql: "CASE WHEN ISJSON([ContentJson]) = 1 THEN JSON_QUERY([ContentJson], '$.citations') END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CitationsJson",
                table: "Artifacts");
        }
    }
}
