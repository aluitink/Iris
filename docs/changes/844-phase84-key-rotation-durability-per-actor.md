# 84.4 — Key-rotation durability + per-actor rotation

**Phase 84** (Production hardening & scale-out readiness). Closes the gap 84.3 left: rotation worked but only for the **instance actor**, and the rotation's **actor→key binding was in-memory only** — a restart silently re-bound the stale key.

## The gap (84.3's two loose ends)

1. **Restart lost the rotation.** The actor→key binding lives in `IKeyProvider` (an `InMemoryKeyProvider`, a plain `Dictionary<Iri, Iri>` — actor IRI → key IRI). It is wiped on process exit. The startup restore path (`WebAppFactory.RestoreLocalSigningKeys`, `RegisterSeedKey`, the sample server) re-derived the binding from a **hard-coded `#key-1`** — not from what the persisted actor document actually advertises. So after a rotation (`#key-1` → `#key-2`) + a restart, the instance actor would re-sign with the **stale** `#key-1` (or fail to resolve once `#key-1` was retired), even though its public document correctly advertised `#key-2`.
2. **Rotation was instance-actor-only.** `POST /ap/v1/keys/rotate` could only rotate the instance actor; there was no way to rotate a specific local user/community actor's key.

## What changed

### 1. `KeyProviderRehydration` (new, `Iris.Server/Identity`)

A stateless library helper (no new DI service) that re-derives every local actor's key binding from its **persisted actor document's `publicKey.id`** — the fragment-aware IRI (83.1), read through `actor.GetPublicKeyIri()` (the 84.2 "single boundary point"):

```csharp
public static Task<int> RehydrateFromActorsAsync(
    IKeyProvider keyProvider, IActorStore actorStore, IKeyStore keyStore,
    CancellationToken ct = default)
```

For each actor from `ListActorsAsync()` it computes `keyIri = actor.GetPublicKeyIri() ?? {actorId}#key-1`, guards with `keyStore.TryGetKey` (only binds keys that actually exist), and calls `keyProvider.RegisterKey(actorIri, keyIri)`. Returns the number of bindings re-registered.

### 2. `WebAppFactory.RestoreLocalSigningKeys` (wired)

The production restart-restore now resolves `IKeyStore` + `IKeyProvider` + `IPersistenceProvider.Actors` from DI and calls `KeyProviderRehydration.RehydrateFromActorsAsync` (a synchronous startup bridge via `.GetAwaiter().GetResult()`, matching the app's existing startup idiom — the host is not yet accepting requests).

**DI note:** `IActorStore` is **not** registered directly in DI (the codebase reads `IPersistenceProvider.Actors`, never a concrete `IActorStore`), so the restore resolves `IPersistenceProvider` and uses `.Actors`.

### 3. `POST /ap/v1/keys/rotate?actor=` (per-actor)

The rotate handler now reads an optional `?actor=` IRI query param. Absent/blank → the instance actor (the 84.3 default, unchanged). Present → must `Iri.TryParse` (else `400`), must exist in the actor store (else `404`), and must be local (enforced by the existing "store contains it" check). The same admin gate (caller must be the instance actor) + degraded-mode gate apply.

**SampleServer + test harness not changed:** the demo sample never rotates (it would lose the seeded key at restart, defeating its purpose — out of scope), and the test harness keeps its `#key-1` convention (the durability test calls the rehydration directly to exercise the restart path).

## Tests (9 new, all server-side)

`KeyProviderRehydrationTests` (4, unit):
- rotated actor → rehydrates to the **current** key (`#key-2`), not the stale `#key-1`.
- legacy actor without `publicKey` → falls back to `#key-1`.
- retired (absent-from-store) key → skipped, not registered.
- empty store → registers nothing.

`KeyRotationDurabilityIntegrationTests` (5, integration, via `ActivityPubHostFactory` + `TestSeeder`):
- **rotate → restart over the same persistence** (host #2 with `RegisterLocalKey=false`) → rehydrate → the actor's signing identity resolves to `#key-2` (not `#key-1`); the pre-rehydration binding is confirmed absent.
- **rotate → retire the old key → restart** → the rotated key still resolves (the retired `#key-1` is gone from the store, but `#key-2` is not).
- `?actor=` rotates a **specific** local actor (its document advertises `#key-2` + `replaces`; the instance actor is untouched).
- `?actor=` for an **unknown** actor → `404` (`application/problem+json`).
- no `?actor=` → the **instance actor** is rotated (84.3 default unchanged).

## Verification

`dotnet build -c Release` → 0 warn / 0 err. `dotnet test -c Release --no-build --filter "Category!=Slow"` → **1779 passed / 0 failed / 1 skipped** (Iris.Server.Tests 1017 → 1026: +9). The one intermittent full-suite failure (a `FollowEdgeConvergence` timing flake under heavy parallel load) is pre-existing, unrelated, and passes in isolation.

## Commits

- `a1cc3a7` — impl + tests.
