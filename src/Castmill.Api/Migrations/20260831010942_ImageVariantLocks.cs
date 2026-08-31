using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ImageVariantLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedAt",
                table: "ImageVariants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedByUserId",
                table: "ImageVariants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageVariants_LockedByUserId",
                table: "ImageVariants",
                column: "LockedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageVariants_LockedByUserId",
                table: "ImageVariants");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "ImageVariants");

            migrationBuilder.DropColumn(
                name: "LockedByUserId",
                table: "ImageVariants");
        }
    }
}
