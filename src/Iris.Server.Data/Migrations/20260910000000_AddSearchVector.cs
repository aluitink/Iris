using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the tsvector column to Actors (nullable — backfilled below).
            migrationBuilder.AddColumn<string>(
                name: "SearchVector",
                table: "Actors",
                type: "tsvector",
                nullable: true);

            // Add the tsvector column to Objects (nullable — backfilled below).
            migrationBuilder.AddColumn<string>(
                name: "SearchVector",
                table: "Objects",
                type: "tsvector",
                nullable: true);

            // GIN index on the tsvector columns for full-text search. Raw SQL because
            // migrationBuilder.CreateIndex does not expose the index method in this EF Core version.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_Actors_SearchVector\" ON \"Actors\" USING gin (\"SearchVector\");");

            migrationBuilder.Sql(
                "CREATE INDEX \"IX_Objects_SearchVector\" ON \"Objects\" USING gin (\"SearchVector\");");

            // Backfill existing rows: build the tsvector from the jsonb Document column's
            // searchable text fields. For Objects: content + name + summary. For Actors:
            // name + preferredUsername + summary. The 'simple' text search configuration
            // does no stemming or stopword removal (matches words literally, case-insensitive).
            //
            // Note: the C# TsVectorBuilder (used for new writes) produces the same tsvector
            // format (word:position pairs). The backfill here uses Postgres' to_tsvector
            // which produces word:position (numeric) — both are valid tsvector values.
            migrationBuilder.Sql(@"
                UPDATE ""Actors""
                SET ""SearchVector"" =
                    setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'name')::text, '')), 'A') ||
                    setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'preferredUsername')::text, '')), 'B') ||
                    setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'summary')::text, '')), 'C')
                WHERE ""SearchVector"" IS NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE ""Objects""
                SET ""SearchVector"" =
                    setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'content')::text, '')), 'A') ||
                    setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'name')::text, '')), 'B') ||
                    setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'summary')::text, '')), 'C')
                WHERE ""SearchVector"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Objects_SearchVector",
                table: "Objects");

            migrationBuilder.DropIndex(
                name: "IX_Actors_SearchVector",
                table: "Actors");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "Objects");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "Actors");
        }
    }
}
