using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class EnforceEvidenceConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                WITH RankedCurrent AS
                (
                    SELECT [Id],
                           ROW_NUMBER() OVER (
                               PARTITION BY [TenantId], [ArtifactId]
                               ORDER BY [CreatedAt] DESC, [Id] DESC) AS [RowNumber]
                    FROM [ContentDependencySnapshots]
                    WHERE [IsCurrent] = 1
                )
                UPDATE snapshots
                SET [IsCurrent] = 0
                FROM [ContentDependencySnapshots] AS snapshots
                INNER JOIN RankedCurrent AS ranked ON ranked.[Id] = snapshots.[Id]
                WHERE ranked.[RowNumber] > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_ContentDependencySnapshots_Current",
                table: "ContentDependencySnapshots",
                columns: new[] { "TenantId", "ArtifactId" },
                unique: true,
                filter: "[IsCurrent] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ContentDependencySnapshots_Current",
                table: "ContentDependencySnapshots");
        }
    }
}
