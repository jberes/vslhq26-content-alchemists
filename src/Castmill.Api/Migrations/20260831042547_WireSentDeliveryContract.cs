using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class WireSentDeliveryContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetricsJson",
                table: "ScheduleEntries",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Permalink",
                table: "ScheduleEntries",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAtUtc",
                table: "ScheduleEntries",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetricsJson",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "Permalink",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "SentAtUtc",
                table: "ScheduleEntries");
        }
    }
}
