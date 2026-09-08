using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationPrefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotificationPrefsJson",
                table: "UserAccounts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationPrefsJson",
                table: "UserAccounts");
        }
    }
}
