# 83.2 — Deployment hardening + config validation (parts 1 + 2)

**Commit:** `582e6c4`
**Status:** parts 1 (config validation) + 2 (observability) COMPLETE. Part 3 (graceful degradation) scoped to a follow-up (see below).

## What was built

Two independent, vertically-complete hardening additions to the server's startup + health surface:

### 1. Startup config validation (fail-fast)

A misconfigured production deployment (a relative `BaseUri`, a non-http(s) `InstanceActorId`, or a malformed `SharedInboxIri`) previously surfaced only later — as a runtime 500 on the first actor-document fetch, or a silently-misrouted federation request. Now it fails **at host start** with an actionable error.

**Key types:**
- `ActivityPubServerOptionsValidator : IValidateOptions<ActivityPubServerOptions>` (`src/Iris.Server/ActivityPubServerOptionsValidator.cs`) — validates, for each of `BaseUri` / `InstanceActorId` / `SharedInboxIri`: *when set*, the value must be an **absolute http(s) IRI**. A null option is allowed (a host may configure a subset and rely on the defaults), so the validator is non-breaking for hosts that only configure some options. The error message names the offending key + the invariant ("must be an absolute http(s) IRI") + why it matters.
- Registered in `AddActivityPubServer` via `services.TryAddEnumerable(Singleton<IValidateOptions<...>, ActivityPubServerOptionsValidator>())` + `services.AddOptions<ActivityPubServerOptions>().ValidateOnStart()`. `ValidateOnStart` runs the validator when the host starts, throwing an `OptionsValidationException` that fails the host before it begins serving.

**Design decision (recorded):** the validator lives in the `Iris.Server` namespace (not a new `Iris.Server.Options` namespace). A new `Iris.Server.Options` namespace collides with `KristofferStrube.ActivityStreams.Create` across the test project (every test file that references the `Create` activity type + imports `Iris.Server` becomes ambiguous). Keeping the validator in `Iris.Server` (where `ActivityPubServerOptions` itself lives) avoids the collision with zero cost.

### 2. Federation observability health check

The instance's `GET /ap/v1/health` endpoint now reports **federation observability**: how many stored actors can actually sign federation (the key-store's resolvable-actor count) and the delivery dead-letter count.

**Key types:**
- `InstanceObservabilityHealthCheck : IHealthCheck` (`src/Iris.Server/Observability/InstanceObservabilityHealthCheck.cs`) — reports three figures in `HealthCheckResult.Data`:
  - `stored_actors` — every actor the instance stores (local actors + communities, via `IActorStore.ListActorsAsync`).
  - `resolvable_actors` — the subset whose signing identity resolves through `IKeyProvider.TryGetIdentity` (i.e. the actors the instance can currently sign federation as). An actor without a registered key (a fresh community, or an actor whose key has not been loaded) is counted as stored-but-not-resolvable, so the operator sees the gap.
  - `dead_letters` — the number of outbound deliveries that exhausted their retry budget and were parked in the dead-letter store (`IDeliveryDeadLetterStore.Count`).
  - Status: `Healthy` when `dead_letters == 0`; `Degraded` when `dead_letters > 0` (a failed-delivery backlog is accumulating — the instance is otherwise healthy, so it is degraded, not unhealthy).
- Registered as an `IHealthCheck` singleton alongside the existing four checks, so the custom `HealthHandler` resolves it through `IEnumerable<IHealthCheck>` (no `UseHealthChecks` required).

**Design decision (recorded):** the resolvable-actor count is computed via `IKeyProvider` (actor → identity), **not** by adding an enumerate/count method to `IKeyStore`. Adding such a method would be a breaking interface change across `InMemoryKeyStore`, `FileBackedKeyStore`, and `EfKeyStore` for a metric that `IKeyProvider` already answers (`TryGetIdentity` is exactly "can this actor sign?"). `Iris.Server` already references `Iris.Client`, so consuming `IKeyProvider` introduces no new dependency direction.

## Part 3 (graceful degradation) — scoped to a follow-up

The slice's third sub-part — "when the durable store is unavailable, the server starts in a read-only/degraded mode (not crash)" — is **not** implemented this turn. Rationale (recorded as the slice's remaining work):

- The existing `PersistenceHealthCheck` already probes a real read against the actor store and reports **Unhealthy** (→ the `/ap/v1/health` endpoint returns **503**) when the store is unreachable. An orchestrator can therefore already detect + evict a degraded instance.
- A true *read-only mode* (the server starts, serves reads, refuses writes) is a substantial architectural change: it requires the write paths (inbound activity handlers, feed service, moderation, follows) to detect a degraded store and reject/queue writes rather than throw, plus a startup probe that distinguishes "store unreachable" from "store healthy but empty." That is a multi-file design that does not fit a vertically-complete slice this turn.
- **Remaining (next turn):** a startup probe that, on a failed persistence read, logs a structured "starting in degraded/read-only mode" event + flips a `IDegradedModeGate` (or similar) that the write paths consult; the health endpoint surfaces the degraded state (it already does, via `PersistenceHealthCheck`). This is seeded as the remaining note on the Active Slice.

## Tests

`tests/Iris.Server.Tests/DeploymentHardeningTests.cs` — **13 new tests**:
- **Config validation (host-start fail-fast):** malformed relative `BaseUri`, non-http(s) `InstanceActorId`, relative `SharedInboxIri` each → host start throws `OptionsValidationException` naming the key + invariant. Valid config → host starts + options resolvable. All-null config → host starts (null allowed).
- **Observability health check:** all-actors-resolvable + no dead letters → Healthy with correct counts; some-actors-not-resolvable → Healthy with the resolvable gap surfaced; dead letters present → Degraded.
- **Validator unit coverage (theory):** 5 cases (valid; malformed actor; malformed base; malformed inbox; all-null) asserting `ValidateOptionsResult.Succeeded`.

## Suite impact

- `dotnet build -c Release` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test -c Release --no-build --filter "Category!=Slow"` — **1608 passed, 0 failed, 1 skipped** (Iris.Server.Tests 975 → 988). The 2 `Iris.Server.Tests` failures observed under heavy parallel full-suite load are the **known timing/contention flakes** (pass 988/988 in isolation and on a clean full-suite run).
