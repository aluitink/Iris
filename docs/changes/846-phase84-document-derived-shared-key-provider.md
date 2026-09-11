# 84.6 (part 1) — Document-derived shared key provider (the convergence primitive)

**Phase 84** (Production hardening & scale-out readiness). 84.5 made the single-instance-per-origin constraint explicit + enforced. This change is the **convergence half** of 84.6 — the first piece: a `IKeyProvider` that converges a running instance to a rotation performed on another instance over the same persistence, **without a restart**.

## The gap (the divergent surface)

84.5's survey found the actor→key binding map is the per-process surface that diverges across two instances over one persistence. After 84.1–84.5:

- The key **material** is shared: both instances read the same `IKeyStore`.
- The actor document's `publicKey.id` is the single durable source of truth for *which* key is current: 84.2's `KeyRotationService.RotateAsync` re-stamps it on every rotation, and the persisted actor document (in the shared `IPersistenceProvider`) is what both instances see.
- The **only** divergent surface is the in-memory `actor → key IRI` map held by each instance's `IKeyProvider` (`InMemoryKeyProvider`, in `Iris.Client`). A rotation on instance A calls `RegisterKey` on **A's** provider only; B's map still points at the old key. B's signer keeps signing with the key A has moved past (and, once the old key is retired, with a key that no longer exists in the store).

84.4's `KeyProviderRehydration` re-derives the map from the persisted documents, but only as a **restart-restore** path (run once at startup). It is not re-run while an instance is live, so a *running* instance B never sees A's rotation.

## What changed

### `DocumentDerivedKeyProvider` (new, `Iris.Server/Identity`)

An `IKeyProvider` that is the convergence-aware provider:

- **`TryGetIdentity(Iri, out IIdentity?)`** (synchronous — the `SigningHandler` resolves identities in a sync call) reads a thread-safe `ConcurrentDictionary<Iri, Iri>` (actor → key IRI) and re-verifies the resolved key against the `IKeyStore` (a key retired since the last refresh is not returned).
- **`RegisterKey(Iri, Iri)`** updates the map — the same seam `KeyRotationService` + the startup restore use, so the provider is a drop-in for the `InMemoryKeyProvider` (the `SigningHandler`, `KeyRotationService`, and restore path all work unchanged).
- **`Task<int> RefreshFromActorsAsync(IActorStore, IKeyStore, CancellationToken ct)`** is the **convergence primitive**: for every stored actor it re-derives the key IRI as `actor.GetPublicKeyIri() ?? {actorId}#key-1` (the `#key-1` fallback is only for a legacy actor with no `publicKey` extension), registers the binding only when the key is present in the store (a retired key is skipped), and **replaces the map atomically** (dropping actors whose key no longer resolves, adding newly-rotated ones). Returns the number of bindings (re)registered.

This is the same logic as `KeyProviderRehydration.RehydrateFromActorsAsync` (the restart-restore path, 84.4) — but **owned by the provider** so it can be re-run to converge a *running* instance.

### Convergence, demonstrated

Two `DocumentDerivedKeyProvider` instances over one shared `InMemoryPersistenceProvider` (its actor store + key store — the topology of two app instances over one origin):

1. Both refresh → both resolve the seeded `#key-1` (baseline).
2. Instance A rotates (a `KeyRotationService` over the shared store mints `#key-2`, stores it in the **shared** key store, re-stamps the actor document, and re-binds **A's** map only).
3. **Divergence:** A resolves `#key-2`; B still resolves `#key-1` (its map was untouched by A's rotation) — the silent failure the provider closes.
4. **Convergence:** B runs `RefreshFromActorsAsync` → B re-derives from the persisted document A re-stamped → B now resolves `#key-2`. **No restart.**

## The sync-resolution constraint (design decision)

`IKeyProvider.TryGetIdentity` is **synchronous** — the sole production caller, `SigningHandler.ResolveIdentity` (in `Iris.Client`), is a sync method, and the server's `DeliveryWorker` signs through that same handler. A document-derived resolver needs the **async** `IActorStore.TryGetActorAsync`. Bridging an async store read inside a sync call (`.GetAwaiter().GetResult()`) is forbidden in library code (CODING_STYLE: no `.Result`/`.Wait()` in library code) and would risk a deadlock on a captured synchronization context.

**Decision:** keep `TryGetIdentity` sync (reading the in-memory map) and make the convergence **explicit + async** (`RefreshFromActorsAsync`). The provider's map is current with the durable documents *as of the last refresh*; convergence happens when an instance refreshes. This keeps the hot sign path sync + allocation-light and pushes the (rare) cross-instance convergence into an explicit, testable, non-blocking call. The **when** of the refresh is the remaining wiring (a periodic hosted service that calls `RefreshFromActorsAsync`, or an on-miss trigger) — recorded as the next turn's piece.

## Tests (3 new, server-side)

`DocumentDerivedKeyProviderConvergenceTests` (`tests/Iris.Server.Tests/Identity`):
- **`RotateOnInstanceA_InstanceB_ConvergesOnRefresh`** — the full story: baseline convergence at `#key-1`; A rotates → A resolves `#key-2` while B is stale at `#key-1` (the divergence is real); B's `RefreshFromActorsAsync` → B resolves `#key-2` (convergence, no restart).
- **`Refresh_SkipsRetiredKey_DropsActorFromMap`** — when the current key is retired (removed from the store) and not re-rotated, the refresh does not (re)bind it; the actor drops out of the map (`TryGetIdentity` fails) rather than pointing at a key that no longer exists.
- **`Refresh_LegacyActorWithoutPublicKey_FallsBackToKey1`** — a legacy actor whose document has no `publicKey` extension is bound to the `#key-1` convention (the key must be present in the store).

## Verification

`dotnet build -c Release` → 0 warn / 0 err. `dotnet test -c Release --no-build --filter "Category!=Slow"` → **0 failed** (Iris.Server.Tests 1040 → 1043: +3).

## Remaining on 84.6 (next turn)

- **The when-of-refresh wiring:** a hosted service (or on-miss trigger) that calls `RefreshFromActorsAsync` periodically / on a resolution miss, so a running instance converges automatically rather than on an explicit call.
- **Shared delivery queue:** a DB/file-backed `IDeliveryQueue` with a consumer claim (a delivery queued on A is delivered by A-or-B, not dropped).
- **Cache invalidation:** a channel so the in-memory actor/edge caches don't serve stale reads across instances.
- **Lift the 84.5 guard** once convergence is complete (single-instance → scale-out supported).

## Commits

- `2909da6` — impl + tests (part 1).
