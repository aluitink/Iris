# 139.3 Scenario 7 — Duplicate/replay delivery idempotency

**Status:** PASS (verified; +1 DB-row-count test closing an evidence gap). No product change needed.

## Bar

> Redeliver the same activity IRI twice; confirm it's stored once (Phase 136.17), no duplicate rows,
> no duplicate UI entries. Evidence: **DB row count + UI check**.

## What the code does (Phase 136.17 / C-07)

The idempotency guard is **add-if-absent by activity IRI** in `InboxProcessor.ProcessAsync`
(`src/Iris.Server/Inbox/InboxProcessor.cs:85-97`):

1. `InboxProcessor.ProcessAsync` calls `Activities.TryAddActivityAsync(delivery.Activity)`.
2. `TryAddActivityAsync` returns `true` only if the activity IRI was *not* already stored; it returns
   `false` when the IRI is already present (a re-delivery).
3. On `false`, the processor **skips handler dispatch entirely** — no re-run, no duplicate edge, no
   duplicate `Accept`, no re-fan-out.
4. The endpoint still returns **202 Accepted** (a re-delivery is a no-op, not a 409/500).
5. `AddToInboxAsync` (recording the activity in the recipient's inbox) is **independently IRI-deduped**
   and gated on `firstDelivery`, so even a bypassed guard couldn't create a duplicate inbox entry.

**Duplicate rows are prevented twice over:**

- The add-if-absent check in the store (`EfActivityStore.TryAddActivityAsync` does
  select-then-insert; `InMemoryActivityStore` uses `ConcurrentDictionary.TryAdd`).
- The **primary keys**: `Activities.Id` (unique) and `BoxItems(Direction, ActorId, ItemIri)` (composite
  unique, `IrisDbContext.cs:107`). A duplicate row is *physically impossible* at the DB level.

## Verification

### DB row count (new test — closes the evidence gap)

The existing tests (`CrossInstanceReplayDefenseIntegrationTests.DuplicateCreateDelivery_StoredOnce_HandlerRunsOnce`,
`DuplicateInboundDeliveryIdempotencyIntegrationTests`) asserted the *logical* no-op
(`TryAddActivityAsync` returns `false` on the second add; inbox item count is 1) but **not the physical
row count**. A regression that inserted a second row while still reporting `false` would have slipped
through.

**Added** `EfPersistenceContractTests.RedeliveredActivity_StoredOnce_SingleRowInDatabase`
(`tests/Iris.Server.Data.Tests/EfPersistenceContractTests.cs`): drives the inbox's add-if-absent twice
(a redelivery) against a real Testcontainers PostgreSQL, then counts the rows in the `Activities` and
`BoxItems` tables **directly** (raw `NpgsqlConnection` `SELECT count(*) WHERE "Id" = @iri` /
`WHERE "ItemIri" = @iri`) and asserts each is exactly **1**. This pins the PK-backed "no duplicate rows"
guarantee and the add-if-absent guard at the physical level.

- `Activities` row count for the redelivered IRI = **1** (was 1 before the redelivery, still 1 after).
- `BoxItems` (inbox) row count for the redelivered IRI = **1** (the recipient's inbox shows it once).

### UI check (live, MCP Playwright, as `andrew`/`Password1`)

The notifications page (`/notifications`) renders **12 distinct notifications** with:

- **No duplicate entries** — each notification has a unique activity IRI; no repeated `Open post` links.
- **No console errors.**
- The UI reads from the deduplicated `BoxItems` inbox, so the DB-level single-row guarantee directly
  implies no duplicate UI entries.

## Evidence

- Test: `EfPersistenceContractTests.RedeliveredActivity_StoredOnce_SingleRowInDatabase` — PASS.
- Full suite: 1306 (Iris.Server.Tests) + 12 (Iris.Server.Data.Tests, +1) + all other projects green,
  0 failures.
- Live UI: 12 distinct notifications, no duplicates, console-clean (Playwright snapshot).

## Note (known, documented, out of scope here)

There is **no signature expiry/freshness check** — a replayed signed request (same `date`/`signature`,
re-sent verbatim) is *accepted* (202). Dedup prevents duplicate *state*, but the request is not rejected
by age. This is the documented Phase 136.17 gap (`docs/changes/13617-phase136-duplicate-replay-defense.md`),
not a scenario-7 failure (the bar is "stored once, no duplicate rows/UI entries," which holds).

## Files

- `tests/Iris.Server.Data.Tests/EfPersistenceContractTests.cs` — +1 test (`RedeliveredActivity_StoredOnce_SingleRowInDatabase`), +`using Npgsql`.
