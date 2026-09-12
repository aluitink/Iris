# Phase 115.5 — Search & Directory: Live Verification + EF Search Bug Fix

## What

Live-verified the search web integration. During verification the global
search endpoint returned **500** on the live (EF/Postgres) host — a genuine
server bug, not just an unreconciled row. Fixed the bug, added a regression
test, rebuilt the app, and re-verified.

## The bug

`GET /ap/v1/search?q=…` 500'd with:

```
System.FormatException: Input string was not in a correct format.
Failure to parse near offset 161. Expected an ASCII digit.
  at EfActorStore.CountSearchMatchesAsync(...)
```

**Root cause.** `EfActorStore.SearchActorsAsync` and
`CountSearchMatchesAsync` built their `FromSqlRaw` SQL with a `$$"""…"""`
*raw-interpolated* string:

```csharp
entities = await db.Set<ActorEntity>().FromSqlRaw<ActorEntity>(
    $$"""
    SELECT * FROM "Actors" WHERE (
        "SearchVector" @@ plainto_tsquery('simple', {0})
        ...
    ) {localClause}ORDER BY ...
    LIMIT {2} OFFSET {3}
    """,
    normalized!, $"%{EscapeLike(normalized!)}%", limit, offset)...
```

In a `$$` raw-interpolated string, `{0}`/`{1}`/`{2}`/`{3}` are **C#
interpolation holes**, not `FromSqlRaw` parameter placeholders. Because
`localClause` was the only *named* hole, C# evaluated the expression and the
`{0}`…`{3}` tokens never survived into the SQL text — the query was malformed
and the ADO.NET layer threw `FormatException` when it tried to bind parameters
to placeholders that were no longer present.

The `EfObjectStore` was **not** affected: it used a verbatim `@"…"` string, so
its `{0}`…`{3}` reached `FromSqlRaw` correctly. The in-memory store (used by
`GlobalSearchIntegrationTests`) never hit the raw SQL at all, so the bug was
invisible to the test suite — only the EF/Postgres production host surfaced it.

## The fix

`src/Iris.Server.Data/Stores/EfActorStore.cs` — both search methods now use
constant **verbatim** string literals (matching the ObjectStore), so the
`{0}`…`{3}` tokens reach `FromSqlRaw` as real parameter placeholders. Because
`FromSqlRaw` requires a *constant* string (EF1003), the `localOnly` branch is
split into two literal queries (each inlines its own `AND "Handle" IS NOT NULL`
for the local-only case) instead of concatenating a variable clause. The now-unused
`LocalOnlySearchClause` constant was removed.

Behavior is unchanged for the in-memory store and for the ObjectStore; only the
previously-broken EF actor path now works.

## Regression test

`tests/Iris.Server.Data.Tests/EfPersistenceContractTests.cs` —
`ActorStore_Search_MatchesAndCounts_DoesNotThrow`: boots a real Postgres
(Testcontainers), seeds a local actor whose `name` contains a unique needle,
then asserts `SearchActorsAsync` + `CountSearchMatchesAsync` return the match in
**both** `localOnly` directions (false and true) and return empty for a
no-match query — in each case without throwing. This is the exact path that
previously threw `FormatException`.

## Live verification (Playwright + curl, after Docker rebuild)

- `GET /ap/v1/search?q=alice` → **HTTP 200**, 9 results (actors + notes) — was 500.
- `GET /ap/v1/search?q=alice&type=Actor` → **HTTP 200**, 3 actor results.
- `/search?q=alice` (global) renders "9 result(s)" with actor + note links, no
  empty state.
- `/search` with the **Actors only** checkbox checked → "3 result(s)", actor
  links only (0 note links).
- No console errors.

## Verification

- `dotnet build` clean.
- `Iris.Server.Data.Tests` 11/0 (incl. the new regression test).
- Full suite: `Iris.Server.Tests` 1105/0 (17 skipped), `Iris.Web.Tests` 95/95,
  `Iris.Core.Tests` 396/0, all other projects green.
- Live app rebuilt (`docker compose build iris-web`) + re-verified (above).

## Matrix reconciliation (D-column)

| Item | Before | After |
|---|---|---|
| Global search (actors + content) | ☐ | ✅ (bug fixed) |
| Actor-only directory filter | ☐ | ✅ |

## Files

- `src/Iris.Server.Data/Stores/EfActorStore.cs` (fix: verbatim FromSqlRaw,
  branched on localOnly; removed `LocalOnlySearchClause`).
- `tests/Iris.Server.Data.Tests/EfPersistenceContractTests.cs` (new regression
  test).
- `docs/plans/production-app-feature-matrix.md` (2 D-column rows reconciled).
