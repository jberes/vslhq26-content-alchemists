using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactRevisionDependencySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ContentDependencySnapshotId",
                table: "ArtifactRevisions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRevisions_ContentDependencySnapshotId",
                table: "ArtifactRevisions",
                column: "ContentDependencySnapshotId");

            migrationBuilder.AddForeignKey(
                name: "FK_ArtifactRevisions_ContentDependencySnapshots_ContentDependencySnapshotId",
                table: "ArtifactRevisions",
                column: "ContentDependencySnapshotId",
                principalTable: "ContentDependencySnapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ArtifactRevisions_ContentDependencySnapshots_ContentDependencySnapshotId",
                table: "ArtifactRevisions");

            migrationBuilder.DropIndex(
                name: "IX_ArtifactRevisions_ContentDependencySnapshotId",
                table: "ArtifactRevisions");

            migrationBuilder.DropColumn(
                name: "ContentDependencySnapshotId",
                table: "ArtifactRevisions");
        }
    }
}
