using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

// Every raw SQL in this test uses either a hardcoded table name (a compile-time test constant) or a
// test-generated IRI value — never user input — so there is no injection surface. EF1002 is
// suppressed for the file rather than sprinkling pragmas at each call site.
#pragma warning disable EF1002

namespace Iris.Server.Data.Tests;

/// <summary>
/// 139.3 Scenario 9 (migration safety): upgrading a populated, older-schema database to the current
/// schema must apply cleanly, lose no data, and leave the app able to boot. The realistic failure
/// modes are a backfill that clobbers existing rows, a column add that drops data, or a raw-SQL
/// migration that errors mid-apply and leaves the schema half-upgraded (so the app cannot boot).
///
/// The test simulates an instance that was running on the very first migration
/// (<c>20260909190837_InitialCreate</c>): it starts a fresh Postgres, applies only that migration,
/// seeds actors + activities (so there is data to preserve and to backfill), then runs the full
/// <c>Migrate()</c> chain (the same call the app makes at startup) and asserts the data survived and
/// the schema is complete. A second test re-runs <c>Migrate()</c> on an already-current database and
/// asserts it is a no-op (idempotent — a restart does not re-apply or corrupt).
/// </summary>
public sealed class MigrationSafetyTests : IAsyncLifetime
{
    private const string InitialMigration = "20260909190837_InitialCreate";

    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithUsername("iris")
            .WithPassword("iris")
            .WithDatabase("iris")
            .Build();
        await container.StartAsync();
        _container = container;
        _connectionString = container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private IrisDbContext NewContext()
        => new(new DbContextOptionsBuilder<IrisDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task UpgradingPopulatedOlderSchema_PreservesDataAndCompletesSchema()
    {
        // --- Simulate an instance running on the first migration only (the "old" version). ---
        await using (var old = NewContext())
        {
            await old.Database.MigrateAsync(InitialMigration);
            Assert.True(await old.Database.CanConnectAsync());
        }

        // --- Seed data the way the old version of the app would have written it. ---
        // The current EF model includes the SearchVector / ObjectIri columns, so it cannot write to the
        // older (InitialCreate) schema — an INSERT would reference columns that do not exist yet. The
        // realistic "old" data was written by an older model, so we insert it via raw SQL matching the
        // InitialCreate columns exactly (no SearchVector / ObjectIri). The jsonb Document shapes mirror
        // what the app stores, so the later backfill SQL has real data to process.
        var suffix = Guid.NewGuid().ToString("N");
        var actorIri = new Iri($"https://mig-safety.local/ap/v1/u/ms-{suffix}");
        var objectIri = $"https://mig-safety.local/ap/v1/objects/mo-{suffix}";
        var activityIri = $"https://mig-safety.local/ap/v1/activities/ma-{suffix}";

        var actorDocument = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = actorIri.Value,
            ["type"] = "Person",
            ["preferredUsername"] = "migsafety",
            ["name"] = "Migration Safety",
        });
        var createDocument = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = activityIri,
            ["type"] = "Create",
            ["actor"] = actorIri.Value,
            ["object"] = new Dictionary<string, object>
            {
                ["id"] = objectIri,
                ["type"] = "Note",
                ["content"] = new[] { "upgrade me please" },
            },
        });

        await using (var seed = NewContext())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Actors\" (\"Id\", \"Handle\", \"Type\", \"CreatedAt\", \"Document\") VALUES (@id, @handle, @type, @created, @doc::jsonb)",
                new NpgsqlParameter("@id", actorIri.Value),
                new NpgsqlParameter("@handle", "migsafety"),
                new NpgsqlParameter("@type", "Person"),
                new NpgsqlParameter("@created", DateTimeOffset.UtcNow),
                new NpgsqlParameter("@doc", actorDocument));

            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Activities\" (\"Id\", \"ActivityType\", \"CreatedAt\", \"Document\") VALUES (@id, @type, @created, @doc::jsonb)",
                new NpgsqlParameter("@id", activityIri),
                new NpgsqlParameter("@type", "Create"),
                new NpgsqlParameter("@created", DateTimeOffset.UtcNow),
                new NpgsqlParameter("@doc", createDocument));
        }

        // Sanity: the old schema has the rows but NOT the columns the later migrations add.
        await AssertRowCountAsync(_connectionString, "Actors", 1);
        await AssertRowCountAsync(_connectionString, "Activities", 1);
        Assert.False(await ColumnExistsAsync(_connectionString, "Actors", "SearchVector"),
            "SearchVector should not exist on the old schema (it is added by a later migration).");
        Assert.False(await ColumnExistsAsync(_connectionString, "Activities", "ObjectIri"),
            "ObjectIri should not exist on the old schema (it is added by a later migration).");

        // --- Upgrade to the current schema: the exact call the app makes at startup. ---
        await using (var current = NewContext())
        {
            await current.Database.MigrateAsync();
            Assert.True(await current.Database.CanConnectAsync());
        }

        // --- Verify: no data loss, schema complete, backfills ran. ---
        // (1) The original rows survived the upgrade (no data loss).
        var actorSurvived = await ScalarAsync(_connectionString,
            "SELECT count(*) FROM \"Actors\" WHERE \"Id\" = @iri AND \"Handle\" = 'migsafety'",
            new NpgsqlParameter("@iri", actorIri.Value));
        Assert.Equal(1, actorSurvived);

        var activitySurvived = await ScalarAsync(_connectionString,
            "SELECT count(*) FROM \"Activities\" WHERE \"Id\" = @iri AND \"ActivityType\" = 'Create'",
            new NpgsqlParameter("@iri", activityIri));
        Assert.Equal(1, activitySurvived);

        // (2) The new columns exist and the backfill SQL processed the seeded rows.
        Assert.True(await ColumnExistsAsync(_connectionString, "Actors", "SearchVector"),
            "The SearchVector column must exist after the upgrade.");
        Assert.True(await ColumnExistsAsync(_connectionString, "Activities", "ObjectIri"),
            "The ObjectIri column must exist after the upgrade.");

        // The AddActivityObjectIri backfill extracts the object IRI from the Activity's
        // jsonb Document; the seeded Create wraps the note, so ObjectIri should be the note's id.
        var objectIriValue = await ScalarStringAsync(_connectionString,
            "SELECT \"ObjectIri\" FROM \"Activities\" WHERE \"Id\" = @iri",
            new NpgsqlParameter("@iri", activityIri));
        Assert.Equal(objectIri, objectIriValue);

        // The AddSearchVector backfill builds a tsvector from the actor's searchable fields;
        // a non-null SearchVector proves the backfill ran (the exact token match is tsvector-internal).
        var searchVectorNonNull = await ScalarBoolAsync(_connectionString,
            "SELECT \"SearchVector\" IS NOT NULL FROM \"Actors\" WHERE \"Id\" = @iri",
            new NpgsqlParameter("@iri", actorIri.Value));
        Assert.True(searchVectorNonNull, "The actor's SearchVector must be backfilled (non-null) after the upgrade.");
    }

    [Fact]
    public async Task MigratingAlreadyCurrentDatabase_IsNoOpAndPreservesData()
    {
        // Fully migrate a fresh database, seed one row, then re-run Migrate() (a restart) and confirm
        // it is idempotent: no error, no data change.
        await using (var first = NewContext())
        {
            await first.Database.MigrateAsync();
        }

        var suffix = Guid.NewGuid().ToString("N");
        var actorIri = new Iri($"https://mig-safety.local/ap/v1/u/restart-{suffix}");
        await using (var seed = NewContext())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Actors\" (\"Id\", \"Handle\", \"Type\", \"CreatedAt\", \"Document\") VALUES (@id, @handle, 'Person', @created, @doc::jsonb)",
                new NpgsqlParameter("@id", actorIri.Value),
                new NpgsqlParameter("@handle", "restart"),
                new NpgsqlParameter("@created", DateTimeOffset.UtcNow),
                new NpgsqlParameter("@doc", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["id"] = actorIri.Value,
                    ["type"] = "Person",
                    ["preferredUsername"] = "restart",
                    ["name"] = "Restart",
                })));
        }

        // A restart re-runs the same startup migration call; it must be a clean no-op.
        await using (var second = NewContext())
        {
            await second.Database.MigrateAsync();
            Assert.True(await second.Database.CanConnectAsync());
        }

        // The row is still there after the no-op re-migration.
        var survived = await ScalarAsync(_connectionString,
            "SELECT count(*) FROM \"Actors\" WHERE \"Id\" = @iri AND \"Handle\" = 'restart'",
            new NpgsqlParameter("@iri", actorIri.Value));
        Assert.Equal(1, survived);
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        var result = (long?)(await cmd.ExecuteScalarAsync())!;
        return (int)result;
    }

    private static async Task<bool> ScalarBoolAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> ScalarStringAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task AssertRowCountAsync(string connectionString, string table, int expected)
    {
        var count = await ScalarAsync(connectionString, $"SELECT count(*) FROM \"{table}\"");
        Assert.Equal(expected, count);
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column)
    {
        // EF quotes identifiers, so the table/column names are case-preserved in the catalog.
        return await ScalarBoolAsync(connectionString,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = @t AND column_name = @c)",
            new NpgsqlParameter("@t", table), new NpgsqlParameter("@c", column));
    }
}
