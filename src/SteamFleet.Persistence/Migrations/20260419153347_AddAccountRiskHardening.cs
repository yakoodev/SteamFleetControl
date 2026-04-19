using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamFleet.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountRiskHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthFailStreak",
                table: "steam_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AutoRetryAfter",
                table: "steam_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRiskAt",
                table: "steam_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRiskReasonCode",
                table: "steam_accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSensitiveOpAt",
                table: "steam_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskLevel",
                table: "steam_accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RiskSignalStreak",
                table: "steam_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_steam_accounts_AutoRetryAfter",
                table: "steam_accounts",
                column: "AutoRetryAfter");

            migrationBuilder.CreateIndex(
                name: "IX_steam_accounts_RiskLevel",
                table: "steam_accounts",
                column: "RiskLevel");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_steam_accounts_AutoRetryAfter",
                table: "steam_accounts");

            migrationBuilder.DropIndex(
                name: "IX_steam_accounts_RiskLevel",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "AuthFailStreak",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "AutoRetryAfter",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "LastRiskAt",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "LastRiskReasonCode",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "LastSensitiveOpAt",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "RiskSignalStreak",
                table: "steam_accounts");
        }
    }
}
