using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoFFPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBehaviorLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BehaviorLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerIp = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MaxConcurrentSessions = table.Column<int>(type: "INTEGER", nullable: false),
                    UniqueAsnCount = table.Column<int>(type: "INTEGER", nullable: false),
                    GeoJumpsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BytesUsedSpike = table.Column<long>(type: "INTEGER", nullable: false),
                    RiskScore = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BehaviorLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BehaviorLogs_ServerIp_Email_Date",
                table: "BehaviorLogs",
                columns: new[] { "ServerIp", "Email", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BehaviorLogs");
        }
    }
}
