using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castmill.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExternalAuthCallbackProof : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExternalAuthAttempts_ExchangeCodeHash",
                table: "ExternalAuthAttempts");

            migrationBuilder.AlterColumn<string>(
                name: "ExchangeCodeHash",
                table: "ExternalAuthAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "CandidateDisplayName",
                table: "ExternalAuthAttempts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateEmail",
                table: "ExternalAuthAttempts",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateProviderKey",
                table: "ExternalAuthAttempts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoopbackReturnUri",
                table: "ExternalAuthAttempts",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthAttempts_ExchangeCodeHash",
                table: "ExternalAuthAttempts",
                column: "ExchangeCodeHash",
                unique: true,
                filter: "[ExchangeCodeHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExternalAuthAttempts_ExchangeCodeHash",
                table: "ExternalAuthAttempts");

            // Attempts are ephemeral; rollback deletes them before restoring the old
            // non-null unique exchange-code column so duplicate empty values cannot fail.
            migrationBuilder.Sql("DELETE FROM [ExternalAuthAttempts];");

            migrationBuilder.DropColumn(
                name: "CandidateDisplayName",
                table: "ExternalAuthAttempts");

            migrationBuilder.DropColumn(
                name: "CandidateEmail",
                table: "ExternalAuthAttempts");

            migrationBuilder.DropColumn(
                name: "CandidateProviderKey",
                table: "ExternalAuthAttempts");

            migrationBuilder.DropColumn(
                name: "LoopbackReturnUri",
                table: "ExternalAuthAttempts");

            migrationBuilder.AlterColumn<string>(
                name: "ExchangeCodeHash",
                table: "ExternalAuthAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthAttempts_ExchangeCodeHash",
                table: "ExternalAuthAttempts",
                column: "ExchangeCodeHash",
                unique: true);
        }
    }
}
