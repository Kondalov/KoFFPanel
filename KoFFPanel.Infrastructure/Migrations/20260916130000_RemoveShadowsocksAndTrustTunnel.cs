using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoFFPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveShadowsocksAndTrustTunnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsShadowsocksEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ShadowsocksLink",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "IsTrustTunnelEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "TrustTunnelLink",
                table: "Clients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsShadowsocksEnabled",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShadowsocksLink",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsTrustTunnelEnabled",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TrustTunnelLink",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
