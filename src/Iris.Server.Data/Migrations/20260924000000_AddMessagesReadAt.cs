using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Iris.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagesReadAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MessagesReadAt",
                table: "UserAccounts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessagesReadAt",
                table: "UserAccounts");
        }
    }
}
