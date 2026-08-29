# 056 — F-13 global search (instance-wide actor + content directory)

> 2026-08-29 · Slice 12.11 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes gap **F-13** (global search / directory): before this slice, an instance only offered
**per-community** search (`GET /ap/v1/c/{name}/search`, Phase 5) — there was no way to discover
actors or content **instance-wide**. A new `GET /ap/v1/search` endpoint searches the instance's own
local surface and serves the result as a paged `OrderedCollection` (the shared
`limit`/`offset` pagination shape, Resolved Decision #6), and the client gains a `SearchAsync`
wrapper.

- **Store listing.** `IActorStore.ListActorsAsync` (every actor this instance stores — its directory)
  and `IObjectStore.ListObjectsAsync` (every stored content object, including `Tombstone`s) are the
  new read surface the search runs over; the in-memory stores implement them by snapshotting their
  backing dictionaries.
- **Search service.** `IGlobalSearchService` / `GlobalSearchService` performs a case-insensitive
  substring match over:
  - each **local actor** — its `name`, `preferredUsername`, and IRI; and
  - each **stored content object** — its `content` and `name`.
  `Tombstone`s are skipped (a deleted object has no searchable content), and an object that is itself
  an `Actor` is skipped in the content pass (it is matched by the actor pass, not duplicated). An
  empty/whitespace query matches **everything** (the endpoint doubles as an unfiltered directory /
  listing). Results are ordered deterministically: actors first, then content objects, each sub-list
  IRI-sorted (ordinal).
- **Endpoint.** `GET /ap/v1/search` reuses the shared `BuildSearchPageDocument` (so page 1 is an
  `OrderedCollection` with `next`, page 2+ an `OrderedCollectionPage` with `prev`/`next`, the last
  page has no `next`, and the query term is recorded under the `iris:searchQuery` extension — the
  exact shape the community search emits). It is computed fresh per request (like the community
  search — not served through the local collection-page cache) and registered in DI via
  `TryAddSingleton<IGlobalSearchService, GlobalSearchService>` (a host may rebind to add ranking,
  full-text indexing, or cross-instance search).
- **Client.** `IActivityPubClient.SearchAsync(instanceBase, query, options)` requests a single page
  (up to `SearchOptions.Limit`, default 100, at `SearchOptions.Offset`) from `{base}/search?q=…` and
  yields its items (actors first, then content). The IRI is derived by `Iri.SearchOf`
  (`{base}/search`); the response is not cached (a search is a fresh query, not a stable collection).

*Scope note:* this searches the **instance's own store** only. It does not query remote instances — a
cross-instance (relay / WebFinger fan-out) search is a distinct, larger feature and is out of scope
for F-13 (it matches the per-community search, which also searches only the local surface).

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/IActorStore.cs` / `InMemoryActorStore.cs` | `ListActorsAsync` (all stored actors). |
| `src/Iris.Server/IObjectStore.cs` / `InMemoryObjectStore.cs` | `ListObjectsAsync` (all stored objects). |
| `src/Iris.Server/IGlobalSearchService.cs` / `GlobalSearchService.cs` | The search (case-insensitive substring over actor + content; skips tombstones / content-pass actors; actors first, IRI-sorted). |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | `GET /ap/v1/search` route + `GlobalSearchHandler`; DI registration of `IGlobalSearchService`. |
| `src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs` | `SearchAsync` (single-page fetch; accepts `OrderedCollection` or `OrderedCollectionPage`). |
| `src/Iris.Client/SearchOptions.cs` | `SearchOptions` record (`Limit`, `BypassCache`, `Offset`). |
| `src/Iris.Core/IriExtensions.cs` | `Iri.SearchOf` (`{base}/search`). |
| `tests/Iris.Server.Tests/GlobalSearchIntegrationTests.cs` | 9 E2E (matches actors+content case-insensitively, by preferredUsername, by name; empty query lists all; no-match empty; `limit`/`offset` paging page 1 `next` / page 2 `prev` no `next` / offset-past-end; client `SearchAsync` round-trip). |
| `tests/Iris.Server.Tests/GlobalSearchServiceTests.cs` | 5 unit (ordering, actor name/IRI matching, content name+content matching, no-match, tombstone + content-pass-actor exclusion). |

## Tests

708 → **722** (+14):

- `tests/Iris.Server.Tests/GlobalSearchIntegrationTests.cs` — 9 new (live `GET /ap/v1/search` over a
  seeded instance: actor + content matching, case-insensitivity, the directory listing, the
  `limit`/`offset` paging shape, and a client `SearchAsync` round-trip).
- `tests/Iris.Server.Tests/GlobalSearchServiceTests.cs` — 5 new (the service in isolation: ordering,
  the matching surfaces, no-match, and the tombstone / content-pass-actor exclusions the
  integration seed does not exercise).

Three existing `IActivityPubClient` test stubs (`FeedServiceTests`, `IrisActorDocumentFetcherTests`,
`IrisRemoteCollectionFetcherTests`) gained a no-op `SearchAsync` to satisfy the widened interface.

## Decisions

- **The search is instance-local (no remote fan-out).** F-13 is the instance's own directory; a
  cross-instance search would need a relay or a WebFinger fan-out and is a separate, larger feature.
  This keeps the scope matched to the per-community search (which also searches only the local
  surface). Recorded inline: a scoping choice with no cross-cutting trade-off.

- **An empty/whitespace query matches everything.** The endpoint doubles as an unfiltered directory
  / listing (useful for a client that wants "all actors + all content" without a term), mirroring
  the community search's empty-query behavior.

- **Actors first, then content, each IRI-sorted.** A deterministic order makes the paged result
  stable across requests (the `limit`/`offset` shape has no ordering of its own) and puts the
  directory (actors) ahead of content, matching a user's discovery intent.
