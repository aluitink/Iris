# 83.4 — Graceful degradation / read-only mode on store-unavailable

**Commit:** `bcc6d05`
**Status:** COMPLETE.

## What was built

When the instance's durable store is unreachable, the server now **degrades gracefully** instead of
crashing or returning 500s on every mutation: it starts (or re-enters) **read-only mode**, serves reads
as normal, and refuses writes with `503 Service Unavailable` (a transient signal a peer should retry on,
not a permanent rejection). This is the carried-forward **part 3** of the 83.2 deployment-hardening slice.

### 1. The degraded-mode gate (stateful)

**`IDegradedModeGate`** (`src/Iris.Server/Observability/IDegradedModeGate.cs`) — reports whether the
instance is currently degraded (read-only). Distinct from the per-request `IReadinessGate`: this is a
*stateful* flag (not a per-request decision), so the write paths read it once per request at O(1).

- `bool IsDegraded` — `true` when writes are refused.
- `void MarkDegraded()` / `void ClearDegraded()` — flip the flag.

**`DefaultDegradedModeGate`** — a single `int` read/written with `Volatile` semantics (`0` = read-write,
`1` = degraded). A torn read is benign: each request sees either the pre-flip or post-flip value, both a
consistent decision for that one request.

### 2. The persistence-degraded-mode probe (a hosted service)

**`PersistenceDegradedModeProbe : BackgroundService`**
(`src/Iris.Server/Observability/PersistenceDegradedModeProbe.cs`) — the component that *detects* the
outage and *flips* the gate:

- **Startup probe:** on host start it performs a real read against the actor store (the same seam a
  file/ or database-backed `IPersistenceProvider` backs). If the read **throws** (the store is down /
  unreachable — *not* merely empty, which is a configuration state), it logs a structured
  `degraded_mode_entered` warning (with the failure detail) + flips the gate to degraded.
- **Periodic re-probe (recovery):** after startup it re-checks on a fixed interval (default **30s**,
  configurable via the constructor's `recheckInterval`). A read that now succeeds **clears** the gate
  (the instance recovers to read-write + logs `degraded_mode_cleared`); a read that fails re-enters it.
  The degraded state is therefore **recoverable without a restart**.
- **Never throws:** a probe failure is itself the degraded signal, not a reason to abort the host. A
  cancelled probe (host stopping) does not flip the gate.

**Design decision (recorded):** the probe is intentionally separate from `PersistenceHealthCheck` (an
`IHealthCheck`). The health check is a per-request, side-effect-free probe that an orchestrator's scrape
drives (it already reports Unhealthy → `/ap/v1/health` 503, so an orchestrator can evict a degraded
instance). The degraded-mode probe is a *background* service that **mutates** the gate (a side effect the
health check must not have). Both share the same read, but only the probe flips the gate.

### 3. The write paths refuse writes when degraded

The four federation **write surfaces** now consult the gate and, when degraded, refuse the write with
`503 Service Unavailable` + an `application/problem+json` body (naming the degraded mode so a peer
retries) **before touching the store** (a degraded store would throw on the write anyway):

| Surface | Endpoint | Precedence |
|---|---|---|
| `InboxHandler` | `POST /ap/v1/u/{handle}/inbox` | degraded check **before** signature validation (an unsigned POST still gets 503, not 401) |
| `CommunityInboxHandler` | `POST /ap/v1/c/{name}/inbox` | same (before signature validation) |
| `OutboxPublishHandler` | `POST /ap/v1/u/{handle}/outbox` | degraded check **after** signature validation (a valid signature is still required — a degraded instance never opens the write surface to unauthenticated clients) |
| `CommunityOutboxPublishHandler` | `POST /ap/v1/c/{name}/outbox` | same (after signature validation) |

**Reads are unaffected** — actor documents, collections, feeds, and the health/ready/dead-letters
endpoints keep serving. This is the "read-only" half of the contract.

### 4. DI registration

In `AddActivityPubServer`:

- `services.TryAddSingleton<IDegradedModeGate, DefaultDegradedModeGate>()` — a host that manages its own
  degraded-state detection may override the gate (TryAdd — an extra/override registration wins).
- `services.TryAddSingleton<PersistenceDegradedModeProbe>()` +
  `services.TryAddSingleton<IHostedService>(sp => sp.GetRequiredService<PersistenceDegradedModeProbe>())`
  — the probe runs for the host's lifetime (startup probe + periodic re-probe).

## Tests

`tests/Iris.Server.Tests/Observability/DegradedModeTests.cs` — **6 new tests**:
- **Gate (statefulness):** default read-write; `MarkDegraded` → degraded; `ClearDegraded` → read-write;
  re-enterable.
- **Probe (failing store → degraded):** a `FailingPersistenceProvider` (whose `IActorStore.TryGetActorAsync`
  throws) + a fresh gate → the startup probe flips the gate to degraded.
- **Probe (healthy store → recovery):** a healthy in-memory store + a pre-marked-degraded gate → the
  startup probe's successful read **clears** the gate (recovery to read-write).
- **Endpoint (degraded inbox write → 503):** gate marked degraded; an unsigned `POST .../inbox` → `503`
  + `application/problem+json` + the body names "degraded".
- **Endpoint (degraded read still served):** gate marked degraded (seeded healthy store); `GET
  /ap/v1/u/alice` (a read) still → 2xx (proves read-only: reads pass, writes are refused).
- **Endpoint (outbox auth precedence):** gate marked degraded; an unsigned `POST .../outbox` → `401`
  (the signature check runs *before* the degraded check — a degraded instance does not open the write
  surface to unauthenticated clients).

## Suite impact

- `dotnet build -c Release` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test -c Release --no-build --filter "Category!=Slow"` — **1620 passed, 0 failed, 1 skipped**
  (Iris.Server.Tests 994 → 1000). The new 6 tests pass in isolation + on a clean full-suite run; the
  occasional `Iris.Server.Tests` failure under heavy parallel full-suite load is the known timing/contention
  flake (passes 1000/1000 in isolation).
