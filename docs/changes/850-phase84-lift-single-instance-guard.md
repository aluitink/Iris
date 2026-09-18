# 84.6 (final) — Lift the single-instance guard for multi-instance scale-out

**Commit:** `4f376f3`
**Date:** 2026-09-11

## What was built

The **lift of the 84.5 single-instance guard** — the final piece of Phase 84.6 (shared-state scale-out).
With all three convergence pieces in place (the document-derived key provider + the shared delivery queue +
the cache-invalidation channel), multi-instance over one origin is now **supported**. The
`SingleInstanceGuardHostedService` no longer fails fast unconditionally on a collision: it resolves the
three convergence pieces from the DI container and, when all three are configured, logs a **warning**
(multi-instance is supported — the instances converge via the shared state) and does **not** fail host
startup. When any is missing, it still fails fast (the instances would diverge on the un-converged surface).

### Key change

`SingleInstanceGuardHostedService` now injects `IServiceProvider`. On a collision (a live different
instance holds the lock), it calls `IsMultiInstanceSupported()`, which resolves the three convergence
pieces by type and checks their concrete types:

| Piece | Registered as | Concrete type (when configured) |
|-------|---------------|----------------------------------|
| Document-derived key provider (part 1) | `IKeyProvider` | `DocumentDerivedKeyProvider` |
| Shared delivery queue (part 2) | `IDeliveryQueue` | `SharedDeliveryQueue` |
| Cache-invalidation channel (part 3) | `ICacheInvalidationPublisher` | `CacheInvalidationChannel` |

- **All three configured** → `IsMultiInstanceSupported()` returns `true` → the guard logs a warning
  (`multi_instance_detected`) and does **not** throw (host startup succeeds).
- **Any missing** → `IsMultiInstanceSupported()` returns `false` → the guard logs an error
  (`single_instance_conflict`) and **re-throws** `InstanceAlreadyRunningException` (host startup fails
  fast).

### Behavior

| Scenario | Before (84.5) | After (84.6) |
|----------|---------------|--------------|
| No lock path configured | Inert (no guard) | Inert (no guard) — unchanged |
| No existing lock | Acquire | Acquire — unchanged |
| Collision + no scale-out pieces | Fail-fast (throw) | Fail-fast (throw) — unchanged |
| Collision + all three scale-out pieces | Fail-fast (throw) | **Warning (no throw)** — the lift |

### Test counts

**1 new integration test** (`tests/Iris.Server.Tests/Bootstrap/SingleInstanceGuardIntegrationTests.cs`):

- `StartAsync_LiveForeignOwner_MultiInstanceConfigured_WarnsNotFails` — the acceptance test: a lock file
  held by a live foreign process + all three scale-out convergence pieces configured (in the DI container)
  → `StartAsync` does **not** throw (the guard logs a warning and proceeds). The foreign owner's lock file
  is untouched (not stolen).

The existing `StartAsync_LiveForeignOwner_FailsFast` test (no scale-out pieces → throw) still passes
(the fail-fast path is unchanged).

**Full suite:** 0 failed (Iris.Server.Tests 1059 → 1060).

## Design decisions

1. **Config-gate the guard (not remove it).** The guard is not removed (it still protects the unsafe
   case: two instances that would diverge on an un-converged surface). It is **config-gated**: fail-fast
   when the scale-out pieces are NOT all configured (the unsafe case), warning when they are (the safe
   case). This preserves the 84.5 protection for the common single-instance deployment (which does not
   configure the scale-out pieces) while enabling the multi-instance deployment (which does).

2. **Resolve the convergence pieces by concrete type (not by a config flag).** The guard checks the
   **concrete types** of the resolved services (`DocumentDerivedKeyProvider`, `SharedDeliveryQueue`,
   `CacheInvalidationChannel`) rather than a config flag (e.g. `Iris:EnableMultiInstance`). This is more
   robust: the guard reflects the **actual** state of the DI container (what the host registered), not a
   separate config key that could drift out of sync. A host that configures the scale-out pieces (via
   `UseDocumentDerivedKeyProvider` + `UseSharedDelivery` + `UseCacheInvalidationChannel`) is automatically
   detected as multi-instance-supported; a host that does not is automatically detected as
   single-instance (fail-fast).

3. **The guard does not acquire the lock when a live foreign instance holds it (in the multi-instance
   case).** When `IsMultiInstanceSupported()` is `true`, the guard logs a warning and **does not** acquire
   the lock (it does not steal the foreign owner's lock file). This is correct: the foreign owner is a
   **peer** (not a stale lock to steal), and the two instances coexist over the shared state. The lock
   file is left for the foreign owner (who will release it on shutdown). If the foreign owner crashes,
   the **next** startup's liveness check will steal the stale lock file (the 84.5 behavior, unchanged).

4. **The warning is informational (not an error).** The `multi_instance_detected` log is at `Warning`
   level (the operator is informed, but the deployment is valid). The `single_instance_conflict` log
   (the fail-fast case) is at `Error` level (the deployment is invalid, and the operator must act).

## Files changed

- `src/Iris.Server/Bootstrap/SingleInstanceGuardHostedService.cs` — the `IServiceProvider` injection +
  the `IsMultiInstanceSupported()` check + the warning-vs-fail-fast logic + the updated XML comments.
- `tests/Iris.Server.Tests/Bootstrap/SingleInstanceGuardIntegrationTests.cs` — the `CreateGuard` helper
  (updated to pass an `IServiceProvider`) + the `CreateMultiInstanceServiceProvider` helper + the new
  `StartAsync_LiveForeignOwner_MultiInstanceConfigured_WarnsNotFails` test.
