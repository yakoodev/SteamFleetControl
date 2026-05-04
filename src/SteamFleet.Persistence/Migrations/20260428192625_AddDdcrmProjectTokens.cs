using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamFleet.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDdcrmProjectTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ddcrm_project_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScopesCsv = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ddcrm_project_tokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ddcrm_project_tokens_ProjectId",
                table: "ddcrm_project_tokens",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ddcrm_project_tokens_Status",
                table: "ddcrm_project_tokens",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ddcrm_project_tokens");
        }
    }
}
