# 113 — Phase 17.1: Health-check endpoint + graceful shutdown

> 2026-08-30 · Phase 17.1 (observability & transport hardening) · `Iris.Server`

## What was built

Phase 17 (change 102) is "observability and transport hardening," split into four sub-slices:
(1) structured logging + OpenTelemetry metrics, (2) circuit breaker + retry hardening, (3) inbound
rate limiting, and (4) **health-check endpoints + graceful shutdown**. This slice is **Part 4** — the
two production-readiness primitives a deployer needs to know "is the instance up, and will it drain
cleanly when I restart it?" — and it adds **no new NuGet packages**: `Microsoft.Extensions.Diagnostics.
HealthChecks` (+ `.Abstractions`) ship in the ASP.NET shared framework, and `IHostedService` is BCL.

Two `IHealthCheck`s are registered and surfaced on a single public endpoint, plus a hosted service that
completes the delivery queue on host stop so the worker drains before the process exits.

## Key types & files

- **`InstanceHealthCheck`** (`src/Iris.Server/Observability/InstanceHealthCheck.cs`) — **new.** The
  liveness check: `Healthy` when the host has a configured `ActivityPubServerOptions.InstanceActorId`,
  `Unhealthy` when it does not (a server without an instance actor cannot serve its own actor document).
  Reports the instance name + actor IRI in `Data`.
- **`DeliveryQueueHealthCheck`** (`src/Iris.Server/Observability/DeliveryQueueHealthCheck.cs`) — **new.**
  Reports the outbound delivery queue's pending count (`IDeliveryQueue.Count`). `Healthy` by default;
  `Degraded` once the count reaches `DeliveryQueueHealthOptions.WarningPending`, `Unhealthy` once it
  reaches `CriticalPending`. Both thresholds default to 0 (= disabled), so an operator opts in to a
  backlog alarm by rebinding the options. The check reports the count in `Data` as `pending`.
- **`DeliveryQueueHealthOptions`** (`src/Iris.Server/Observability/DeliveryQueueHealthOptions.cs`) —
  **new.** `WarningPending` / `CriticalPending` (int, 0 = disabled).
- **`DeliveryQueueShutdownService`** (`src/Iris.Server/Observability/DeliveryQueueShutdownService.cs`) —
  **new.** An `IHostedService` whose `StopAsync` calls `IDeliveryQueue.CompleteAsync` so the
  `DeliveryWorker` (a `BackgroundService`) observes a complete-and-empty queue and exits its dequeue loop,
  draining in-flight jobs before the host stops. `StartAsync` is a no-op. It is registered **before**
  `DeliveryWorker` in DI; the generic host stops hosted services in reverse registration order, so the
  queue completes while the worker is still alive to drain.
- **`ActivityPubServerConstants`** — added `HealthRouteSegment = "health"` (the `/ap/v1/health` route
  segment).
- **`ActivityPubServerExtensions`** — registered `DeliveryQueueHealthOptions` + the two
  `IHealthCheck`s (as `AddSingleton<IHealthCheck, T>()`, one per type) +
  `AddHostedService<DeliveryQueueShutdownService>()` (before the worker), and mapped
  `GET /ap/v1/health` (no ActivityPub signature) to a `HealthHandler` that runs every registered
  `IHealthCheck` and aggregates the result.

## How it works

- **The endpoint runs the checks without the framework's `UseHealthChecks` runner.** `HealthHandler`
  resolves `IEnumerable<IHealthCheck>` from DI and calls `CheckHealthAsync(new HealthCheckContext(), ct)`
  on each (the `.Abstractions` API; in .NET 10 `HealthCheckContext` has a parameterless constructor and a
  single `Registration` property, and Iris's checks do not read it). A check that throws is recorded as
  `Unhealthy` (a faulting probe must not 500 the health endpoint). The aggregate status is `Unhealthy`
  if any check is `Unhealthy`, else `Degraded` if any is `Degraded`, else `Healthy`.
- **Status → HTTP mapping:** `Healthy` / `Degraded` → `200 OK`; `Unhealthy` → `503 Service
  Unavailable`. The body is `{"status":"healthy|degraded|unhealthy","checks":{name:{"status","description"}}}`
  (JSON, `application/json`, UTF-8). `degraded` still returns 200 — a backlog warning is not a
  "take me out of rotation" signal; only `unhealthy` is.
- **The endpoint is public (no signature).** A load balancer / orchestrator / `docker-compose healthcheck`
  probe must reach it unsigned. It sits under `/ap/v1/` so the group-level `Iris-Version` header filter
  applies, but it is outside the signature-validated inbox/object routes.
- **Graceful drain on stop.** Host shutdown → the generic host calls `StopAsync` on hosted services in
  reverse registration order. `DeliveryQueueShutdownService` is registered before `DeliveryWorker`, so it
  stops *after* the worker's `StopAsync` begins cancelling its loop, but the queue is completed so the
  worker's dequeue loop (which exits on a complete-and-empty queue) finishes any in-flight delivery and
  returns rather than being hard-cancelled. The result: pending jobs are drained (or dead-lettered by the
  Phase 16.2 file-backed queue) before the process exits, instead of being dropped mid-delivery.

## Tests

13 new tests in `tests/Iris.Server.Tests/Observability/`:

- **`HealthCheckUnitTests`** (7): `DeliveryQueueHealthCheck` — empty → Healthy, below-warning → Healthy,
  at-warning → Degraded, at-critical → Unhealthy, thresholds-disabled (default) → Healthy at any count;
  `InstanceHealthCheck` — configured → Healthy (with `instance`/`actor` in `Data`), no-actor → Unhealthy;
  `DeliveryQueueShutdownService` — `StopAsync` completes the queue (empty queue's `TryDequeueAsync` →
  `null`), and `StartAsync` is a no-op (the queue stays open and an enqueued job is still dequeueable).
- **`HealthEndpointIntegrationTests`** (4): `GET /ap/v1/health` against a live `TestServer` — empty queue
  → 200 `healthy` with both `InstanceHealthCheck` + `DeliveryQueueHealthCheck` entries present; pending
  at the warning threshold → 200 `degraded` (queue check `degraded`); pending at the critical threshold →
  503 `unhealthy` (queue check `unhealthy`); and the response carries the `Iris-Version` header. The
  integration test binds a `FixedCountQueue` test double (settable `Count`, `TryDequeueAsync` never yields
  a job) so the hosted `DeliveryWorker` cannot drain the "backlog" the test sets, isolating the endpoint's
  reporting from the worker's real-time draining.

Test count: 1074 → **1087** total (full suite green; all prior tests still pass unchanged).

## Decisions

- **No new NuGet package.** `Microsoft.Extensions.Diagnostics.HealthChecks` (+ `.Abstractions`) are part
  of the `Microsoft.AspNetCore.App` shared framework (verified present at
  `/usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.10/`), and `IHostedService` /
  `IHostApplicationLifetime` are BCL. The repo's rule — no new package without a ROADMAP note +
  justification — is satisfied with zero new packages.
- **Run the checks manually, not via `UseHealthChecks`.** The framework's `UseHealthChecks` middleware +
  `AddHealthChecks().AddCheck(...)` registration would also work, but it forces a specific registration
  shape (the `AddHealthChecks()` builder) and a named-check registry. Iris instead registers each
  `IHealthCheck` as a plain singleton and runs them in the endpoint. This keeps the DI surface minimal
  (a host that *also* wants the standard `/health` middleware can `AddHealthChecks()` independently and
  get both), and the Iris endpoint stays a lightweight, no-runner path. The trade-off (a context-reading
  third-party check registered via `UseHealthChecks` gets the framework's full context, whereas the Iris
  endpoint passes a default context) is documented on the handler and is acceptable because Iris's own
  checks do not read the context.
- **`Degraded` is 200, not 503.** Only `Unhealthy` takes the instance out of rotation. A delivery backlog
  at the warning threshold is a "watch this" signal (visible in the body + `Iris-Version`-tagged
  telemetry), not a "drop the connection" signal — mapping it to 503 would cause an LB to shed load from a
  merely-busy-but-functional instance.
- **The shutdown service is a separate `IHostedService`, not logic in the worker's `StopAsync`.** Putting
  the queue completion in the worker's own `StopAsync` would race the worker's own dequeue loop (the
  worker would be completing the queue it is trying to drain). A dedicated service that stops *after* the
  worker's `StopAsync` has begun (reverse registration order) cleanly sequences: worker stops accepting
  new work → queue completes → worker's loop drains in-flight jobs and exits.
- **The `DeliveryQueueHealthCheck` reads `Count`, not `Jobs`.** `IDeliveryQueue.Count` is the stable,
  cheap, `O(1)`-ish property on the seam (the in-memory queue's `Reader.Count`, the file-backed queue's
  journal length). The `Jobs` snapshot (drain-and-reinsert) is a test/inspection affordance, not a
  production read.

## Files changed

- `src/Iris.Server/Observability/InstanceHealthCheck.cs` — **new**
- `src/Iris.Server/Observability/DeliveryQueueHealthCheck.cs` — **new**
- `src/Iris.Server/Observability/DeliveryQueueHealthOptions.cs` — **new**
- `src/Iris.Server/Observability/DeliveryQueueShutdownService.cs` — **new**
- `src/Iris.Server/ActivityPubServerConstants.cs` — `HealthRouteSegment`
- `src/Iris.Server/ActivityPubServerExtensions.cs` — health-check + shutdown registrations, `GET /ap/v1/health`, `HealthHandler`
- `tests/Iris.Server.Tests/Observability/HealthCheckUnitTests.cs` — **new** (7 tests)
- `tests/Iris.Server.Tests/Observability/HealthEndpointIntegrationTests.cs` — **new** (4 tests)
