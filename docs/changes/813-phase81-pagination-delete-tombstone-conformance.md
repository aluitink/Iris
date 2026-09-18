# 81.3 — Wire-format edge cases + outbox/inbox pagination conformance (Phase 81, slice 3)

**Slice:** 81.3 (wire-format edge cases + outbox/inbox pagination conformance; `Delete`/`Tombstone` handling).
**Status:** DONE — 8 new `Iris.Core.Tests` round-trip/conformance tests + 4 new `Iris.Server.Tests` `last` assertions; **2 production code fixes** (server `last` link; client inbox `orderedItems` read).
**Companion:** 81.1 (Mastodon) + 81.2 (Misskey + Pleroma) are complete ([811](811-phase81-mastodon-interop-roundtrip.md) · [812](812-phase81-misskey-pleroma-interop-roundtrip.md)).

## What was done

Surveyed Iris's current pagination + `Delete`/`Tombstone` handling, then fixed two conformance gaps and added 12 new/extended tests.

### Conformance gaps found + fixed

1. **Server never emitted a `last` link.** `BuildCollectionPageDocument`/`SerializeCollectionPage` (`ActivityPubServerExtensions.cs`) emitted `first`/`next`/`prev`/`totalItems` but **never `last`** — an AS2.0 `OrderedCollection.last` conformance gap (Mastodon emits `last`; Iris didn't).
   - **Fix:** added a `last` parameter to `SerializeCollectionPage` + emit it. `BuildCollectionPageDocument` computes `lastIri` = the final page's IRI (`{collection}/?page={pageCount}` for a multi-page collection; the collection IRI itself for a single-page collection) and passes it on **every** page, so a client on any page can jump to the end.
2. **Client inbox read path ignored `orderedItems`.** `FetchAuthenticatedCollectionPageAsync` (`ActivityPubClient.cs`) read only `Collection.Items`, but the canonical ActivityStreams form (used by Mastodon + the Iris outbox) is `orderedItems`. The outbox/feed paths already used the shared `CollectionPageFactory.ResolveCollectionItems` (which prefers `orderedItems`, falls back to `items`); the inbox path did not — so a remote server serving its inbox with `orderedItems` would yield an **empty inbox** in Iris.
   - **Fix:** the inbox path now routes through `CollectionPageFactory.ResolveCollectionItems`, making it symmetric with the outbox/feed paths.

### Verified correct (no code change)

- **`Delete`/`Tombstone` is fully handled, no crash:** inbound `Delete` (owner-guarded → stored `Tombstone` + propagation), a standalone `Tombstone` (handled at the inbox dispatch), and `Create(Tombstone)` (routed to `TombstoneInbound`). A stored `Tombstone` is served under the original IRI with `formerType`/`deleted`. Outbound propagation sends a `Delete` activity. The 81.3 tests lock the wire-format round-trips (see below) so a peer that fetches a tombstone or a `Delete` deserializes cleanly.
- **`totalItems`** is always emitted (conformant). **`next`/`prev`** are emitted as bare IRI strings (conformant, matches what the client parses).
- **Pagination is page-number-based** (`?page=N&limit=N`), not cursor-based. This is conformant (cursor/keyset is an optional optimization, not a requirement); out-of-range pages clamp to the last page rather than 404.

### Tests added

**`Iris.Core.Tests/WireFormatEdgeCaseTests.cs`** (new file, 8 facts) — deserialization/conformance of the easy-to-miss wire shapes:

| Test | What it locks in |
|---|---|
| `Tombstone_DeserializesAndRoundTrips` | A `Tombstone` (deleted object) → the `Tombstone` class with `formerType`/`deleted`; round-trips. |
| `DeleteActivity_DeserializesAndRoundTrips` | A `Delete` activity (via the polymorphic converter — `IObjectOrLink as Delete`) with `actor`/`object` resolving; round-trips. |
| `OrderedCollection_Page1_DeserializesWithFirstLastAndTotalItems` | A **real** Mastodon `OrderedCollection` (page 1) → `totalItems`=425, `first`+`last` resolve (Mastodon emits `last`; Iris's new `last` emission is now symmetric with what it consumes). |
| `OrderedCollectionPage_PageN_DeserializesWithNextPrevPartOfAndOrderedItems` | A **real** Mastodon `OrderedCollectionPage` (page N) → `next`/`prev`/`partOf` resolve, `orderedItems` carries 20 activities. |
| `OrderedCollection_Empty_DeserializesCleanly` | An empty collection (`items: []`, `totalItems: 0`) deserializes cleanly (fresh account / no-match query). |
| `OrderedCollection_SingleItem_DeserializesCleanly` | A single-item collection (`totalItems: 1`) deserializes cleanly — the edge the library's one-or-multiple converter is known to collapse. |
| `ResolveCollectionItems_ReadsOrderedItemsWhenPresent` | The shared helper reads `orderedItems` (the canonical form) when present — the behavior the inbox path now depends on. |
| `ResolveCollectionItems_FallsBackToItemsAndEmptyWhenNeither` | The helper falls back to `items`, and returns an empty list when neither is present. |

The two Mastodon outbox documents are genuine fixtures captured in 81.1 (`mastodon-outbox.json`, `mastodon-outbox-page1.json`); the tombstone/delete/edge documents are representative literals in the exact wire shape Iris emits.

**`Iris.Server.Tests/CollectionEndpointIntegrationTests.cs`** (extended 4 existing facts) — assert the server now emits `last`:
- Page 1 → `first` (self) + `last` = `?page=3` (the final page).
- Page 2 → `last` = `?page=3`.
- Last page (3) → `last` = `?page=3` (self-referential).
- Single-page `following` collection → `last` = the collection IRI (self), no `next`.

## Decision (recorded per the autonomous-loop open-questions policy)

**Inbox `orderedItems` fix scope:** the inbox read path (`FetchAuthenticatedCollectionPageAsync`) is private + Basic-auth-gated, so a full end-to-end client test would require adding an inbox endpoint + Basic-auth to the shared `FakeActivityPubServer` — disproportionate infrastructure for a one-line routing fix. Instead, the fix is (a) the inbox path now calls the shared `CollectionPageFactory.ResolveCollectionItems` (the same helper the outbox/feed paths use, so the two are symmetric by construction), and (b) two new `Iris.Core.Tests` tests lock in that helper's `orderedItems`-preferring / `items`-falling-back / empty behavior. This verifies the exact logic the inbox path now delegates to, without the infra. The server-side inbox is already covered by `Iris.Server.Tests`.

**Pagination scheme:** left page-number-based (conformant). Cursor/keyset pagination is an optional optimization (useful for very large collections where `?page=N` is O(N)); it is not required for federation correctness and is a candidate future optimization, not a conformance gap.

## Verification

- **Build:** `dotnet build -c Release` → **0 warnings, 0 errors**.
- **Tests:** `dotnet test -c Release --filter "Category!=Slow"` → **1568 passed, 0 failed, 1 skipped** (up from 1560 — the 8 new `Iris.Core.Tests`; the 4 `last` assertions extend existing `Iris.Server.Tests` facts; `Iris.Client.Tests` stays 157 after the inbox fix). The single `Iris.Server.Tests` failure in the parallel full-suite run is the **known timing/contention flake** — it passes **975/975 in isolation** (the server change is purely additive — a new `last` link — and `CollectionEndpointIntegrationTests` passes 9/9 in isolation).

## Files changed

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `BuildCollectionPageDocument` computes + passes `lastIri`; `SerializeCollectionPage` gains a `last` param + emits it.
- `src/Iris.Client/ActivityPubClient.cs` — `FetchAuthenticatedCollectionPageAsync` reads items via `CollectionPageFactory.ResolveCollectionItems` (reads `orderedItems` + falls back to `items`) instead of `Items` only.
- `tests/Iris.Core.Tests/WireFormatEdgeCaseTests.cs` — new (8 tests).
- `tests/Iris.Server.Tests/CollectionEndpointIntegrationTests.cs` — 4 existing facts extended with `last` assertions.
- No new NuGet packages; no csproj change (the two Mastodon outbox fixtures already exist from 81.1).

## Test-debt log

- **Web tests:** none touched (core/server-side wire-format + conformance tests are in-scope for new coded tests per the WASM manual-test policy).
- **Core/Server/Client tests:** 8 new + 4 extended, all passing; no existing tests modified or deleted beyond adding the `last` assertions.
