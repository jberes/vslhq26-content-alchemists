using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class CitationsNestedContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CitationsJson",
                table: "Artifacts",
                type: "nvarchar(max)",
                nullable: true,
                computedColumnSql: "CASE WHEN ISJSON([ContentJson]) = 1 THEN COALESCE(JSON_QUERY([ContentJson], '$.citations'), JSON_QUERY([ContentJson], '$.content.citations')) END",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true,
                oldComputedColumnSql: "CASE WHEN ISJSON([ContentJson]) = 1 THEN JSON_QUERY([ContentJson], '$.citations') END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CitationsJson",
                table: "Artifacts",
                type: "nvarchar(max)",
                nullable: true,
                computedColumnSql: "CASE WHEN ISJSON([ContentJson]) = 1 THEN JSON_QUERY([ContentJson], '$.citations') END",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true,
                oldComputedColumnSql: "CASE WHEN ISJSON([ContentJson]) = 1 THEN COALESCE(JSON_QUERY([ContentJson], '$.citations'), JSON_QUERY([ContentJson], '$.content.citations')) END");
        }
    }
}
