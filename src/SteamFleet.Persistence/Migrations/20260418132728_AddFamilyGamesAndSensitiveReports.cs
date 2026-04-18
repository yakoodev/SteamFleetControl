using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamFleet.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyGamesAndSensitiveReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GamesCount",
                table: "steam_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GamesLastSyncAt",
                table: "steam_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentAccountId",
                table: "steam_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileUrl",
                table: "steam_accounts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "job_sensitive_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    EncryptedPayload = table.Column<string>(type: "text", nullable: false),
                    EncryptionVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_sensitive_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_sensitive_reports_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "steam_account_games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PlaytimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    ImgIconUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_account_games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_steam_account_games_steam_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "steam_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_steam_accounts_ParentAccountId",
                table: "steam_accounts",
                column: "ParentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_job_sensitive_reports_ConsumedAt",
                table: "job_sensitive_reports",
                column: "ConsumedAt");

            migrationBuilder.CreateIndex(
                name: "IX_job_sensitive_reports_JobId",
                table: "job_sensitive_reports",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_steam_account_games_AccountId_AppId",
                table: "steam_account_games",
                columns: new[] { "AccountId", "AppId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_steam_account_games_LastSyncedAt",
                table: "steam_account_games",
                column: "LastSyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_steam_account_games_Name",
                table: "steam_account_games",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_steam_accounts_steam_accounts_ParentAccountId",
                table: "steam_accounts",
                column: "ParentAccountId",
                principalTable: "steam_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_steam_accounts_steam_accounts_ParentAccountId",
                table: "steam_accounts");

            migrationBuilder.DropTable(
                name: "job_sensitive_reports");

            migrationBuilder.DropTable(
                name: "steam_account_games");

            migrationBuilder.DropIndex(
                name: "IX_steam_accounts_ParentAccountId",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "GamesCount",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "GamesLastSyncAt",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "ParentAccountId",
                table: "steam_accounts");

            migrationBuilder.DropColumn(
                name: "ProfileUrl",
                table: "steam_accounts");
        }
    }
}
