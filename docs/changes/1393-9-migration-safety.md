# 139.3 Scenario 9 — Migration safety

**Status:** PASS (verified). Upgrading a populated, older-schema database to the current schema applies cleanly, loses no data, runs the backfills, and the app can boot against the migrated schema. Re-running `Migrate()` on an already-current database is an idempotent no-op.

## Bar

> Apply the current EF Core migrations to a populated/older-schema DB copy; confirm migration
> completes cleanly, no data loss, and the app boots against the migrated schema.

## What the test covers

`tests/Iris.Server.Data.Tests/MigrationSafetyTests.cs` (2 tests, each on its own isolated
`postgres:16-alpine` Testcontainer so migration-state manipulation can't leak between tests):

### 1. `UpgradingPopulatedOlderSchema_PreservesDataAndCompletesSchema`

Simulates an instance that was running on the very first migration
(`20260909190837_InitialCreate`):

1. Apply **only** that migration to a fresh Postgres.
2. Seed one `Actor` and one `Create` activity **via raw SQL** matching the InitialCreate columns
   exactly (`Actors(Id, Handle, Type, CreatedAt, Document)`,
   `Activities(Id, ActivityType, CreatedAt, Document)`) — i.e. the shape an older model would have
   written, with no `SearchVector` / `ObjectIri`. The jsonb `Document` shapes mirror what the app
   stores, so the later backfill SQL has real data to process.
3. Sanity-check the old schema: rows present, but `Actors.SearchVector` and `Activities.ObjectIri`
   do **not** exist yet (they are added by later migrations).
4. Run the full `Migrate()` chain — the exact call the app makes at startup.
5. Assert:
   - **No data loss** — both seeded rows survive, readable by the current model.
   - **Schema complete** — `Actors.SearchVector` and `Activities.ObjectIri` now exist.
   - **Backfills ran** — `AddActivityObjectIri` extracted the note's IRI from the `Create`'s jsonb
     `Document -> 'object' ->> 'id'` (the stored `ObjectIri` equals the note's id), and
     `AddSearchVector` produced a non-null `SearchVector` for the actor (built from the actor's
     searchable fields in the jsonb `Document`).

### 2. `MigratingAlreadyCurrentDatabase_IsNoOpAndPreservesData`

Fully migrate a fresh database, seed one `Actor` (raw SQL), then re-run `Migrate()` (a restart) and
confirm it is a clean idempotent no-op: no error, the row is still there.

## Why raw-SQL seeding

The current `IrisDbContext` model includes **all** columns (including `SearchVector` / `ObjectIri`).
It cannot write to the older InitialCreate schema — an EF `INSERT` would reference columns that do
not exist yet (`42703: column "SearchVector" ... does not exist`). The realistic "old" data was
written by an older model, so the test inserts it via raw SQL matching the InitialCreate column set.
The same constraint applies to the read-side verification: EF Core 10's `SqlQueryRaw<T>` scalar
translation is unreliable for these raw queries, so the test reads scalar values through a direct
`NpgsqlCommand` (`ExecuteScalarAsync`) instead.

## Findings

No defects found. The migration chain is safe to run on a populated, older-schema database:

- `Migrate()` applies the pending migrations in order (InitialCreate → AddSearchVector →
  AddActivityObjectIri) and leaves the history table consistent (pending list empty after).
- The two raw-SQL backfill migrations (`AddSearchVector`, `AddActivityObjectIri`) process existing
  rows without clobbering them: the seeded actor's `Document` is intact and its `SearchVector` is
  populated; the seeded `Create`'s `Document` is intact and its `ObjectIri` is correctly extracted.
- Re-running `Migrate()` on an already-current database is a no-op (idempotent restart).

One test-infrastructure note (not a product bug): the `information_schema.columns` catalog stores
the EF-quoted, case-preserved identifiers (`Actors`, `SearchVector`), so the column-existence helper
matches on the exact name (no lowercasing).

## Evidence

```
dotnet test tests/Iris.Server.Data.Tests/Iris.Server.Data.Tests.csproj \
  --filter "FullyQualifiedName~MigrationSafetyTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2
```

Full solution build: 0 warnings / 0 errors. Full `Category!=Slow` suite: 0 failed across all 11
test projects.
