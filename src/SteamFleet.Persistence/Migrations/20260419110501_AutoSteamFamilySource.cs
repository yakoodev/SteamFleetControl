using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamFleet.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutoSteamFamilySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_steam_accounts_steam_accounts_ParentAccountId",
                table: "steam_accounts");

            migrationBuilder.DropIndex(
                name: "IX_steam_accounts_ParentAccountId",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "ParentAccountId",
                table: "steam_accounts");

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "steam_accounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FamilySyncedAt",
                table: "steam_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExternal",
                table: "steam_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFamilyOrganizer",
                table: "steam_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SteamFamilyId",
                table: "steam_accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SteamFamilyRole",
                table: "steam_accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_steam_accounts_IsExternal",
                table: "steam_accounts",
                column: "IsExternal");

            migrationBuilder.CreateIndex(
                name: "IX_steam_accounts_SteamFamilyId",
                table: "steam_accounts",
                column: "SteamFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_steam_accounts_SteamId64",
                table: "steam_accounts",
                column: "SteamId64");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_steam_accounts_IsExternal",
                table: "steam_accounts");

            migrationBuilder.DropIndex(
                name: "IX_steam_accounts_SteamFamilyId",
                table: "steam_accounts");

            migrationBuilder.DropIndex(
                name: "IX_steam_accounts_SteamId64",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "FamilySyncedAt",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "IsExternal",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "IsFamilyOrganizer",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "SteamFamilyId",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "SteamFamilyRole",
                table: "steam_accounts");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentAccountId",
                table: "steam_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_steam_accounts_ParentAccountId",
                table: "steam_accounts",
                column: "ParentAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_steam_accounts_steam_accounts_ParentAccountId",
                table: "steam_accounts",
                column: "ParentAccountId",
                principalTable: "steam_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
