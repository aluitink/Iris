# 117.3 — Directory: Persist Remote Actors for "All Known" Scope

## Summary

The directory's "All known" scope previously returned the same results as "This instance"
because remote actors were never persisted to the durable `Actors` table. They lived only in
the in-memory `RemoteActorCache` (1h TTL, capacity 1024), which is not queryable for the
directory search.

This change adds a `RemoteActorPersister` that persists remote actor documents to the durable
`IActorStore` on first encounter, wired into the `IActorDocumentFetcher` so that any remote
actor fetched during federation (signature validation, delivery, community feed) is
automatically added to the directory's "All known" surface.

## Changes

### RemoteActorPersister (new)

`src/Iris.Server/Security/RemoteActorPersister.cs`

- `PersistIfNewAsync(Actor?, CancellationToken)`:
  - Returns `false` for null actors or actors without an `Id`
  - Skips local actors (IRI prefix matches the instance base)
  - Checks `TryGetActorAsync` first — if already stored, returns `false` (idempotent, no overwrite)
  - Otherwise calls `PutActorAsync` and returns `true`
  - Persistence failures are logged (warning) but not thrown — the fetch succeeds regardless

### IrisActorDocumentFetcher (modified)

- New optional `RemoteActorPersister?` constructor parameter
- After a successful fetch (cache miss or hit), if a persister is provided, calls
  `PersistIfNewAsync` on the actor. This means:
  - First fetch: miss → fetch from network → persist to durable store
  - Subsequent fetches: cache hit → no re-persist (the actor is already in the durable store)

### ActivityPubServerExtensions (DI wiring)

- The `IActorDocumentFetcher` registration now creates a `RemoteActorPersister` when
  `IPersistenceProvider` is available, passing the instance `BaseUri` as the local-prefix
  discriminator.

### Tests (new)

`tests/Iris.Server.Tests/Security/RemoteActorPersisterTests.cs` — 11 tests:

| Test | Verifies |
|---|---|
| `PersistIfNew_RemoteActor_StoresInDurableStore` | Remote actor is stored |
| `PersistIfNew_LocalActor_Skips` | Local actor (IRI prefix) is not stored |
| `PersistIfNew_AlreadyStored_IsIdempotent` | Re-persist is a no-op |
| `PersistIfNew_NullActor_ReturnsFalse` | Null actor → false |
| `PersistIfNew_ActorWithoutId_ReturnsFalse` | Missing IRI → false |
| `PersistIfNew_NullInstanceBase_PersistsAll` | No base → all persisted |
| `PersistIfNew_GroupActor_Persists` | Group actors work |
| `PersistIfNew_MultipleActors_AllPersisted` | Batch persistence |
| `PersistIfNew_DoesNotOverwriteExisting` | Original doc preserved |
| `PersistIfNew_DifferentInstanceBase_SkipsThatInstance` | Correct base used |
| `PersistIfNew_PrefixBoundary_DoesNotFalseMatch` | Prefix check is exact |

## Design Decisions

1. **Persist on fetch, not on inbox receipt.** The `IActorDocumentFetcher` is the single choke
   point for all remote actor document retrieval (signature validation, delivery, community
   feed, follow-feed). Persisting here captures every remote actor the instance encounters,
   regardless of how it was triggered. An alternative (persisting in each inbox handler)
   would miss actors fetched for key resolution that never result in a delivered activity.

2. **No overwrite on re-fetch.** The persister uses `TryGetActorAsync` before `PutActorAsync`,
   so an actor's first-seen document is preserved. Updates to remote actors flow through
   `UpdateActivityHandler` (which already does a read-modify-write on the durable store).
   This avoids a race where a stale cached fetch overwrites a fresher Update.

3. **Best-effort persistence.** A DB failure during persist is logged but does not fail the
   fetch. The in-memory `RemoteActorCache` continues to serve the document for its TTL
   regardless. The actor will be re-attempted on the next cache miss.

4. **IRI prefix for local/remote discrimination.** Consistent with the existing
   `GlobalSearchService.GetLocalActorsFilteredAsync` approach (Phase 105). The instance base
   (e.g. `https://iris.luit.ink/ap/v1`) is compared via `StartsWith` — reliable even when a
   remote actor carries a `preferredUsername`.

## Verification

- `dotnet build` — 0 errors, 0 warnings
- `dotnet test` — 1,125 tests pass (11 new + 1,114 existing; known flaky federation tests pass in isolation)
- Live Playwright verification on `http://localhost:8088/directory`:
  - "This instance" shows only local actors (alice, andrew, bob, verifier87)
  - "All known" shows local + remote actors (RayvenMX from mastodon.world, Low Quality Facts, etc.)
  - Toggling between scopes works correctly
  - No console errors
