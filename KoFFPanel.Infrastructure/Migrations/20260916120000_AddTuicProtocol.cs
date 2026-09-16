using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoFFPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTuicProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTuicEnabled",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TuicLink",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTuicEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "TuicLink",
                table: "Clients");
        }
    }
}
