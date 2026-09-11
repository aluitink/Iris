# 84.5 — Multi-instance (scale-out) state readiness: single-instance-per-origin guard

**Phase 84** (Production hardening & scale-out readiness). 84.1–84.4 made the instance durable across a *single* host's restart (dead-letter journal, rotation lifecycle + operator endpoints, restart-safe rehydration, per-actor rotation). This slice is the "scale-out" half: **what happens when two app instances share one origin / one persistence.**

## The survey (the decision driver)

The per-process state surfaces an Iris instance holds:

| Surface | Impl | Shared across two processes? |
|---|---|---|
| Actor/edge graph | `IPersistenceProvider` | Only the EF/Postgres provider; the in-memory + file-backed providers each hold a **disjoint in-memory copy of the whole graph** (not safe for two processes at all). |
| Key *material* | `IKeyStore` (the RSA pairs, persisted) | Yes — both instances read the same key bytes from the shared store. |
| actor→key **binding map** | `IKeyProvider` (`InMemoryKeyProvider`, a `Dictionary<Iri,Iri>`) | **No** — each process holds its own map. 84.4's rehydration fixes the *restart* case (re-derive from the persisted document) but two *concurrent* instances each hold a disjoint copy: a rotation on instance A is **invisible to B's signer** (B keeps signing with the old key IRI). |
| Delivery queue | `IDeliveryQueue` (in-memory unless `UseFileBackedDelivery`) | **No** — a delivery queued on A is never sent by B. |
| In-memory actor/edge caches | per-process | **No** — stale reads until each cache expires. |

**Conclusion:** a true scale-out (two instances converging) requires a *shared* key provider + a *shared* delivery queue + a shared cache invalidation channel — a multi-slice sub-system (a document-derived key provider that both processes read from the same store on every sign, a DB/file-backed queue with a consumer claim, a pub/sub cache-invalidation bus). That is seeded as **84.6**.

The **84.5** decision (Option 3 from the slice) is the *safe, honest* first step: Iris **does not yet support two concurrent instances on one origin.** Rather than let a second instance silently diverge (two instances signing the same actor with different keys, each dropping the other's deliveries — a data-corrupting failure with no error), Iris now **converts that configuration error into a loud, actionable startup failure.** A single instance per origin is the documented, enforced constraint until 84.6 lands the shared state.

## What changed

### 1. `SingleInstanceLock` (new, `Iris.Server/Bootstrap`)

A cross-process lock that owns a **lock file** (JSON: the owning process id + a host identifier). `AcquireAsync(lockPath, hostIdentifier, ct)`:

- **no lock file** → create it + acquire;
- **lock file whose owner process is dead** (the prior instance exited/crashed and didn't clean up) → **steal** it (overwrite) + acquire — a crash never leaves a stale lock that bricks the instance;
- **lock file whose owner is alive and is a different process** → **throw** `InstanceAlreadyRunningException` (the host fails to start).

`Dispose()` deletes the lock file **only if it still names this instance** (a concurrent steal overwrote it → don't delete the other instance's lock). Liveness is the portable, P/Invoke-free check: `Process.GetProcessById` throws `ArgumentException` for a dead pid, so a crashed instance's handle (closed by the OS) is gone and a new instance can steal. The known PID-recycle window (the prior owner's id reassigned to an unrelated process before a new instance starts) would be misread as "alive"; its consequence is only a *safe* failed startup that the operator resolves by deleting the stale lock file.

`LockRecord` + `IsProcessAlive` are public (the lock-file format is something an operator inspects when debugging a stuck instance; `IsProcessAlive` is a reusable utility).

### 2. `SingleInstanceGuardHostedService` (new, `Iris.Server/Bootstrap`)

A hosted service that calls `SingleInstanceLock.AcquireAsync` on `StartAsync` (propagating `InstanceAlreadyRunningException` so the **host fails fast**) and disposes the lock on `StopAsync` (releasing the file). No-op when no lock path is configured (defensive — it should only be registered when the path is set).

### 3. Config: `Iris:InstanceLockPath`

`ActivityPubServerOptions.InstanceLockPath` + the `Iris:InstanceLockPath` config binding. `AddActivityPubServer(IConfiguration)` registers the guard **only when the key is set and non-empty**. The default (no key) leaves the guard **inert** — existing multi-host test harnesses and single-process deployments are unaffected. This is opt-in: a deployment that runs one instance per origin (the supported topology) sets the key to get the guard; the in-memory/file-backed test harnesses (which run many hosts per process) do not.

## Tests (14 new, all server-side)

`SingleInstanceLockTests` (11, unit — `tests/Iris.Server.Tests/Bootstrap`):
- acquire when no lock file exists (file created, names this pid);
- steal a stale lock whose owner is **our own** pid (a leftover from a prior run of this process);
- steal a lock whose owner process is **dead** (a crashed instance's leftover);
- **fail fast** when a **live foreign** process holds the lock (`InstanceAlreadyRunningException` with the owner's pid + host; the owner's file is untouched);
- steal an **unparseable** lock file (treat as stale);
- `Dispose` deletes **our** lock file;
- `Dispose` preserves **another** instance's lock file (a concurrent steal);
- creates a missing parent directory for the lock path;
- `IsProcessAlive` reports the current process alive + a dead pid not alive;
- empty/whitespace lock path → `ArgumentException`.

`SingleInstanceGuardIntegrationTests` (3, integration — the exact DI-registered hosted service, real `StartAsync`/`StopAsync` against a real lock file + a real live foreign process):
- **fail-fast**: a lock held by a live foreign process → `StartAsync` throws `InstanceAlreadyRunningException` (the owner's file is untouched);
- **acquire + release**: `StartAsync` acquires (file names this process) → `StopAsync` deletes it;
- **inert**: no `InstanceLockPath` configured → `StartAsync`/`StopAsync` are no-ops.

**Why the integration test drives the hosted service directly (not two full `TestServer`s):** the guard is cross-*process* — two hosts in one test process share `Environment.ProcessId`, so the second would *steal* the first's lock (the "same pid → stale" rule) instead of failing. A live *foreign* process is what makes the fail-fast path observable, which is exactly the production scenario (a second container/host on the same persistence). The unit tests cover the raw lock's full state machine; the integration tests cover the hosted-service lifecycle the DI container actually starts.

## Verification

`dotnet build -c Release` → 0 warn / 0 err. `dotnet test -c Release --no-build --filter "Category!=Slow"` → **0 failed** (Iris.Server.Tests 1026 → **1040**: +14).

## Commits

- `77b4006` — impl + tests.

## Follow-up (84.6)

The guard makes the single-instance constraint explicit + enforced, but true scale-out (two instances converging) still needs the shared state: a document-derived **shared key provider** (both processes resolve the actor→key from the same persisted store on every sign, so a rotation on A is visible to B's signer without a restart), a **shared delivery queue** (DB/file-backed with a consumer claim, so a delivery queued on A is delivered by A-or-B), and a **cache-invalidation** channel. Seeded as the next Active Slice.
