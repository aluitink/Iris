# Phase 105 — Directory Improvements

## What was built

Fixed the Directory page's "This instance" / "All known" scope toggle so the two views show **different data**. Previously both tabs showed the same actors because the `localOnly` discriminator used the presence of `preferredUsername` to distinguish local from cached remote actors — but remote actors from other platforms (Mastodon, Lemmy, Pleroma) also carry a `preferredUsername`, so they were incorrectly included in "This instance".

## Key changes

- **`GlobalSearchService`** (`src/Iris.Server/Services/GlobalSearchService.cs`):
  - New optional `Iri? instanceBase` constructor parameter (the instance's base IRI, e.g. `https://iris.luit.ink`).
  - When `localOnly` is true **and** `instanceBase` is available, the service fetches all actors from the store (`localOnly: false`) and filters by **IRI prefix** (the actor's IRI must start with the instance base IRI). This correctly identifies local actors regardless of whether they carry a `preferredUsername`.
  - When `instanceBase` is null, falls back to the store's `preferredUsername` heuristic (backwards compatible).
  - Query matching, ordering, and content-pass logic are unchanged.

- **DI registration** (`src/Iris.Server/ActivityPubServerExtensions.cs`):
  - `IGlobalSearchService` is now registered as a factory that captures `IOptions<ActivityPubServerOptions>.Value.BaseUri` and passes it to the `GlobalSearchService` constructor.

## Test counts

- **4 new unit tests** in `GlobalSearchServiceTests`:
  - `Search_LocalOnly_WithInstanceBase_ExcludesRemoteActorsWithPreferredUsername` — a remote actor with a `preferredUsername` is excluded from "This instance" when the IRI doesn't match the instance base.
  - `Search_LocalOnly_WithInstanceBase_IncludesLocalActors` — local actors (IRI prefix matches) are included.
  - `Search_LocalOnly_WithInstanceBase_FallsBackToStoreHeuristic_WhenBaseIsNull` — backwards compatibility.
  - `Search_LocalOnly_WithInstanceBase_QueryFilter` — query matching works with IRI-prefix filtering.
- Full fast suite: **1419 passed / 0 failed** (up from 1415 with the 4 new tests).

## Live verification (Playwright)

- "This instance" (default): 4 local people (alice, andrew, bob, verifier87) — all `iris.luit.ink` IRIs. No remote actors.
- "All known": 5 people — the same 4 plus `localhost:8088/alice` (a leftover actor from a prior dev run with a different base URI). The two tabs now show **different data**.
- Communities tab: 4 local communities, all correct.
- 0 console errors.

## Decisions

- **IRI prefix over host matching**: Used `Id.StartsWith(instanceBase)` rather than parsing and comparing hosts. This is simpler and handles the case where the instance base IRI includes a path (e.g., `https://example.com/iris`). The `TrimEnd('/')` on the prefix prevents a trailing-slash mismatch.
- **Service-level filtering, not store-level**: The IRI-prefix filter is applied in the `GlobalSearchService` (which has access to the instance base IRI) rather than in the `IActorStore` (which does not). The store is called with `localOnly: false` to get all actors, then the service filters. For a typical instance (hundreds of actors, not millions) this is performant. If the actor count ever becomes a concern, the filtering can be pushed into the store by adding an `Iri? instanceBase` parameter to the store's search methods.
- **Backwards compatibility**: When `instanceBase` is null (e.g., a host that doesn't set `BaseUri`), the service falls back to the store's `preferredUsername` heuristic. Existing tests that construct `GlobalSearchService` without an `instanceBase` continue to pass.
