using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExternalAuthFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId_LoginProvider",
                table: "AspNetUserLogins",
                columns: new[] { "UserId", "LoginProvider" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "ExternalAuthAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ClientKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReturnRouteKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CodeChallenge = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PollSecretHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExchangeCodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LinkUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalAuthAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalAuthAttempts_AspNetUsers_LinkUserId",
                        column: x => x.LinkUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ExternalAuthAttempts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthAttempts_ExchangeCodeHash",
                table: "ExternalAuthAttempts",
                column: "ExchangeCodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthAttempts_LinkUserId",
                table: "ExternalAuthAttempts",
                column: "LinkUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthAttempts_Status_ExpiresAt",
                table: "ExternalAuthAttempts",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthAttempts_UserId",
                table: "ExternalAuthAttempts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalAuthAttempts");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUserLogins_UserId_LoginProvider",
                table: "AspNetUserLogins");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");
        }
    }
}
