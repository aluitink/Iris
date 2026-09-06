using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ActivityType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Actors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Handle = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Actors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BoxItems",
                columns: table => new
                {
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ItemIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Position = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoxItems", x => new { x.Direction, x.ActorId, x.ItemIri });
                });

            migrationBuilder.CreateTable(
                name: "CreateIndex",
                columns: table => new
                {
                    ObjectId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreateActivityId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreateIndex", x => x.ObjectId);
                });

            migrationBuilder.CreateTable(
                name: "Edges",
                columns: table => new
                {
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Target = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Edges", x => new { x.Kind, x.Source, x.Target });
                });

            migrationBuilder.CreateTable(
                name: "Keys",
                columns: table => new
                {
                    KeyId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrivateKeyPem = table.Column<string>(type: "text", nullable: true),
                    PublicKeyPem = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Keys", x => x.KeyId);
                });

            migrationBuilder.CreateTable(
                name: "Media",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Objects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    AttributedTo = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ObjectType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsTombstoned = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Objects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ActivityType",
                table: "Activities",
                column: "ActivityType");

            migrationBuilder.CreateIndex(
                name: "IX_Actors_Handle",
                table: "Actors",
                column: "Handle");

            migrationBuilder.CreateIndex(
                name: "IX_Edges_Kind_Source_Target",
                table: "Edges",
                columns: new[] { "Kind", "Source", "Target" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Edges_Kind_Target",
                table: "Edges",
                columns: new[] { "Kind", "Target" });

            migrationBuilder.CreateIndex(
                name: "IX_Media_Id",
                table: "Media",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Objects_AttributedTo",
                table: "Objects",
                column: "AttributedTo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "Actors");

            migrationBuilder.DropTable(
                name: "BoxItems");

            migrationBuilder.DropTable(
                name: "CreateIndex");

            migrationBuilder.DropTable(
                name: "Edges");

            migrationBuilder.DropTable(
                name: "Keys");

            migrationBuilder.DropTable(
                name: "Media");

            migrationBuilder.DropTable(
                name: "Objects");
        }
    }
}
