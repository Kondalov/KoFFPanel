using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoFFPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerIp = table.Column<string>(type: "TEXT", nullable: false),
                    IsAntiFraudEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsP2PBlocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    Uuid = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiryDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    VlessLink = table.Column<string>(type: "TEXT", nullable: false),
                    IsVlessEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHysteria2Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hysteria2Link = table.Column<string>(type: "TEXT", nullable: false),
                    IsTrustTunnelEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrustTunnelLink = table.Column<string>(type: "TEXT", nullable: false),
                    TrafficUsed = table.Column<long>(type: "INTEGER", nullable: false),
                    TrafficLimit = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    LastIp = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveConnections = table.Column<int>(type: "INTEGER", nullable: false),
                    LastOnline = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerIp = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    Country = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrafficLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerIp = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BytesUsed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrafficLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ViolationLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerIp = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ViolationType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViolationLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionLogs_ServerIp_Email_IpAddress",
                table: "ConnectionLogs",
                columns: new[] { "ServerIp", "Email", "IpAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_TrafficLogs_ServerIp_Email_Date",
                table: "TrafficLogs",
                columns: new[] { "ServerIp", "Email", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ViolationLogs_ServerIp_Email",
                table: "ViolationLogs",
                columns: new[] { "ServerIp", "Email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropTable(
                name: "ConnectionLogs");

            migrationBuilder.DropTable(
                name: "TrafficLogs");

            migrationBuilder.DropTable(
                name: "ViolationLogs");
        }
    }
}
