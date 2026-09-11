# 84.6 (part 1b) — Key-provider refresh hosted service (auto-convergence)

**Phase 84.6, shared-state scale-out — the "when" of the refresh.** Commit `84e6f9a`.

## Problem

Part 1 (846) shipped the **convergence primitive**: `DocumentDerivedKeyProvider.RefreshFromActorsAsync`
re-derives every local actor's key IRI from the durable actor document's `publicKey.id`, so a rotation
performed on *another* instance over the same persistence becomes visible here — no restart. But a
primitive is only useful if something **calls** it.

The natural caller is the `SigningHandler`'s identity resolution — but `IKeyProvider.TryGetIdentity` is
**synchronous** (the `SigningHandler` resolves identities in a sync call, and the server's `DeliveryWorker`
signs through it), while re-deriving the key from the durable document is an **async** read
(`IActorStore.ListActorsAsync`). CODING_STYLE forbids sync-over-async (`.Result` / `.GetAwaiter()
.GetResult()`) in library code, so the signer cannot do the document read itself. The *when* of the
refresh therefore has to live in a background service that the signer is decoupled from.

## What was built

**`KeyProviderRefreshService`** (`Iris.Server/Identity`, new) — a `BackgroundService` (the
`PersistenceDegradedModeProbe` pattern: a startup pass + a periodic re-run loop):

- **On host start** (before the interval loop) and then **every `ActivityPubServerOptions
  .KeyProviderRefreshInterval`** (default **30 s**; a non-positive value disables the periodic pass but
  still runs the startup pass), the service:
  1. resolves the instance's `IKeyProvider` from DI;
  2. if it is a `DocumentDerivedKeyProvider`, re-runs
     `RefreshFromActorsAsync(persistence.Actors, persistence.Keys, ct)` over the shared
     `IPersistenceProvider`;
  3. any other `IKeyProvider` (the default `InMemoryKeyProvider`, or a host-registered
     `DelegatingKeyProvider`) is **left untouched** — the service is then a no-op.
- **Best-effort:** a failed document read (a transient persistence blip) is logged at `Warning` and the
  next tick retries; it never throws into the host. A cancelled refresh (host stopping) is not logged as a
  failure.

This makes the auto-convergence **opt-in by provider choice**: a host that wants multi-instance convergence
registers the `DocumentDerivedKeyProvider` as its `IKeyProvider` and (optionally) sets
`Iris:KeyProviderRefreshInterval`; the common single-instance deployment (which keeps the
`InMemoryKeyProvider` default) is unaffected — the service is a no-op for it.

## Key types / wiring

| Type | Role |
|---|---|
| `KeyProviderRefreshService` (new) | The hosted service; the "when" of the refresh. |
| `ActivityPubServerOptions.KeyProviderRefreshInterval` (new) | The tick interval (default 30 s; non-positive = startup-only). |
| `AddActivityPubServer` | Registers the service **unconditionally** (it self-gates to a no-op for non-document-derived providers) + binds `Iris:KeyProviderRefreshInterval` from config. |

## Tests (3 new, `Iris.Server.Tests/Identity/KeyProviderRefreshServiceAutoConvergenceTests.cs`)

1. **`RotateOnInstanceA_InstanceB_ConvergesAutomatically_NoExplicitRefresh`** — the acceptance test. Two
   instances over one origin (a shared `InMemoryPersistenceProvider`): A rotates (its own
   `DocumentDerivedKeyProvider` + a `KeyRotationService` over the shared store); B's provider is registered
   as the `IKeyProvider` a `KeyProviderRefreshService` resolves. The test starts the service, rotates on A,
   and then **polls B** (bounded by a 10 s deadline, 25 ms cadence) until B's `TryGetIdentity` resolves the
   **new** key — with **no explicit `RefreshFromActorsAsync` call** in the test. The service's periodic pass
   (a 50 ms interval in the test) is what re-derives B's map. Proves the production behavior: a rotation on
   A is visible to B's signer on its own.
2. **`Inert_WhenProviderIsNotDocumentDerived_NoRefreshRuns`** — the service is a no-op when the
   `IKeyProvider` is the default `InMemoryKeyProvider`: it does not throw across ticks, and a rotation on the
   shared store is **not** pulled into the in-memory provider (which still resolves the original key it was
   registered with). The single-instance deployment is unaffected.
3. **`NonPositiveInterval_DisablesPeriodicRefresh_ButStartupPassStillConverges`** — a `KeyProviderRefresh
   Interval` of zero disables the periodic tick, but the **startup** convergence pass still runs: a fresh
   (empty) `DocumentDerivedKeyProvider` converges to the seeded actor on start, with the periodic loop off.

## Design decision (recorded)

**The service is registered unconditionally in `AddActivityPubServer`, self-gated by the provider type,
rather than config-gated.** A config gate (e.g. only register when `Iris:KeyProviderRefreshInterval` is set)
would couple "is multi-instance convergence on" to a config key, but the *real* opt-in is the provider
choice (a host that does not register a `DocumentDerivedKeyProvider` does not want convergence). Registering
unconditionally keeps the gate in one place (the `is not DocumentDerivedKeyProvider` check), keeps the
service resolvable by name in tests, and leaves the default single-instance path byte-for-byte unchanged (the
service is a no-op). The interval config remains as a tuning knob, not an on/off switch.

## Not yet done (84.6 remaining)

- **(b) a shared delivery queue** — a DB/file-backed `IDeliveryQueue` with a consumer claim (a delivery
  queued on A is delivered by A-or-B, not dropped).
- **(c) a cache-invalidation channel** — so the in-memory actor/edge caches don't serve stale reads across
  instances.

When (b)+(c) land, the 84.5 single-instance guard can be lifted (made a warning, not a failure, or documented
as supported scale-out).
