using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityObjectIri : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ObjectIri",
                table: "Activities",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ActivityType_ObjectIri",
                table: "Activities",
                columns: new[] { "ActivityType", "ObjectIri" });

            // Backfill existing rows: extract the activity's object IRI from the jsonb Document column.
            // "object" can be a single object (most activities) or an array (rare).
            // For a single object, read "id" directly. For an array, read the first entry's "id" or "href".
            // Activities without an object property (e.g. bare Undo) get NULL.
            migrationBuilder.Sql(@"
                UPDATE ""Activities""
                SET ""ObjectIri"" = (""Document"" -> 'object' ->> 'id')
                WHERE ""ObjectIri"" IS NULL
                  AND ""Document"" ? 'object'
                  AND jsonb_typeof(""Document"" -> 'object') = 'object'
                  AND ""Document"" -> 'object' ? 'id';
            ");

            migrationBuilder.Sql(@"
                UPDATE ""Activities""
                SET ""ObjectIri"" = (""Document"" -> 'object' -> 0 ->> 'id')
                WHERE ""ObjectIri"" IS NULL
                  AND ""Document"" ? 'object'
                  AND jsonb_typeof(""Document"" -> 'object') = 'array'
                  AND ""Document"" -> 'object' -> 0 ? 'id';
            ");

            migrationBuilder.Sql(@"
                UPDATE ""Activities""
                SET ""ObjectIri"" = (""Document"" -> 'object' -> 0 ->> 'href')
                WHERE ""ObjectIri"" IS NULL
                  AND ""Document"" ? 'object'
                  AND jsonb_typeof(""Document"" -> 'object') = 'array'
                  AND ""Document"" -> 'object' -> 0 ? 'href';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_ActivityType_ObjectIri",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ObjectIri",
                table: "Activities");
        }
    }
}
