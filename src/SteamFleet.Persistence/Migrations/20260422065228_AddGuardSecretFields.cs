using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamFleet.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuardSecretFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedDeviceId",
                table: "steam_account_secrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedLinkStatePayload",
                table: "steam_account_secrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedRevocationCode",
                table: "steam_account_secrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedSerialNumber",
                table: "steam_account_secrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedTokenGid",
                table: "steam_account_secrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedUri",
                table: "steam_account_secrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GuardFullyEnrolled",
                table: "steam_account_secrets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedDeviceId",
                table: "steam_account_secrets");

            migrationBuilder.DropColumn(
                name: "EncryptedLinkStatePayload",
                table: "steam_account_secrets");

            migrationBuilder.DropColumn(
                name: "EncryptedRevocationCode",
                table: "steam_account_secrets");

            migrationBuilder.DropColumn(
                name: "EncryptedSerialNumber",
                table: "steam_account_secrets");

            migrationBuilder.DropColumn(
                name: "EncryptedTokenGid",
                table: "steam_account_secrets");

            migrationBuilder.DropColumn(
                name: "EncryptedUri",
                table: "steam_account_secrets");

            migrationBuilder.DropColumn(
                name: "GuardFullyEnrolled",
                table: "steam_account_secrets");
        }
    }
}
