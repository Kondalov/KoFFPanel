using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoFFPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrojanAndShadowsocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsShadowsocksEnabled",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTrojanEnabled",
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

            migrationBuilder.AddColumn<string>(
                name: "TrojanLink",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsShadowsocksEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "IsTrojanEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ShadowsocksLink",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "TrojanLink",
                table: "Clients");
        }
    }
}
