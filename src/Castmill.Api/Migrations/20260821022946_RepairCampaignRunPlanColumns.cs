using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class RepairCampaignRunPlanColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH(N'[Campaigns]', N'Intent') IS NULL
                    ALTER TABLE [Campaigns] ADD [Intent] nvarchar(30) NULL;

                IF COL_LENGTH(N'[Campaigns]', N'OutputRecipeJson') IS NULL
                    ALTER TABLE [Campaigns] ADD [OutputRecipeJson] nvarchar(4000) NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
