# 130 — Phase 17.2: structured delivery metrics

**Status:** DONE — the `DeliveryService` and `DeliveryWorker` now record outbound-delivery metrics
(enqueued / delivered / attempt-failed / dead-lettered) into a shared `IrisDeliveryMetrics` instance
(via DI). No new NuGet dependency — the counters are plain `Interlocked`-based `long` fields
(thread-safe, zero allocation). A host that wants to export the metrics (Prometheus, OTLP, etc.)
subscribes to `SnapshotChanged` and reads `Snapshot`, or — once the `System.Diagnostics.Metering` API
is available on the target runtime — wraps this class in an OTel `MeterProvider`.

## Background — Phase 17.2

Phase 17 (observability and transport hardening) has four sub-slices:
- **17.1** (done, change [113](113-s17-1-health-checks-graceful-shutdown.md)): health-check endpoints + graceful shutdown.
- **17.2** (this change): structured logging + metrics.
- **17.3**: circuit breaker + retry hardening.
- **17.4**: inbound rate limiting.

The "structured logging" half of 17.2 was **already done** — all 13 log calls in `Iris.Server` use
proper structured message templates (PascalCase placeholders, exception-first-arg, no string
interpolation). The "metrics" half was greenfield: no `Meter`, no `Counter`, no `Histogram`, no
`ActivitySource` anywhere in the repo.

### Why `Interlocked` counters, not the `Meter` API

The `System.Diagnostics.Metering` namespace (`Meter`, `Counter<T>`, `Histogram<T>`, `MeterListener`)
is **not available** in this trimmed .NET 10 environment (the `System.Diagnostics.Metering.dll` ref
assembly is absent from the shared framework — the `System.Diagnostics.DiagnosticSource.dll` ref
assembly does not export the `System.Diagnostics.Metering` namespace). Adding a `PackageReference`
to `System.Diagnostics.DiagnosticSource` to get the metering API would be a new NuGet dependency
(the repo's `Directory.Build.props` uses central package management + lock files +
`TreatWarningsAsErrors`), and the `Meter` API is only useful if a host is going to export the
metrics via OpenTelemetry — which is a host-level concern, not a library-level one.

Instead, `IrisDeliveryMetrics` uses plain `Interlocked`-based `long` counters (thread-safe, zero
allocation, no dependency) + a `ConcurrentDictionary` for per-label breakdowns + a
`SnapshotChanged` event. This is observable in tests (read `Snapshot`) and exportable by a host
(subscribe to `SnapshotChanged`, or wrap in an OTel `MeterProvider` once the API is available).

## Change

- **`src/Iris.Server/Observability/IrisDeliveryMetrics.cs`** (new) — the metrics class. Four
  cumulative counters (`Enqueued`, `Delivered`, `AttemptFailed`, `DeadLettered`) + per-activity-type
  breakdown (`ByActivityType`) + per-failure-kind breakdown (`ByFailureKind`). A `SnapshotChanged`
  event fires on each record. A `Snapshot` property returns a point-in-time
  `DeliveryMetricsSnapshot` record. No OpenTelemetry dependency.

- **`src/Iris.Server/Delivery/DeliveryService.cs`** — the `DeliverAsync` method now calls
  `RecordEnqueued(activityType)` after enqueueing (at the same point it already logs).

- **`src/Iris.Server/Delivery/DeliveryWorker.cs`** — the `DeliverOneAsync` method now calls
  `RecordDelivered(activityType)` on success (2xx), `RecordAttemptFailed(activityType, kind)` on a
  failed attempt (non-2xx or transport error), and `DeadLetterAsync` now calls
  `RecordDeadLettered(activityType, kind)` when the retry budget is exhausted.

- **`src/Iris.Server/ActivityPubServerExtensions.cs`** — `IrisDeliveryMetrics` is registered as a
  singleton (`TryAddSingleton<IrisDeliveryMetrics>()`) and injected into the `DeliveryService` and
  `DeliveryWorker` factory lambdas (alongside the existing dependencies).

## Tests

- **`tests/Iris.Server.Tests/Observability/DeliveryMetricsUnitTests.cs`** (new, 8 tests):
  - `Snapshot_Fresh_IsAllZeros` — a fresh instance has all-zero counters.
  - `RecordEnqueued_IncrementsTheEnqueuedCounter` — `RecordEnqueued` increments `Enqueued` + the
    per-activity-type breakdown.
  - `RecordDelivered_IncrementsTheDeliveredCounter` — `RecordDelivered` increments `Delivered`.
  - `RecordAttemptFailed_IncrementsTheAttemptFailedCounterAndTheFailureKind` — `RecordAttemptFailed`
    increments `AttemptFailed` + the per-failure-kind breakdown.
  - `RecordDeadLettered_IncrementsTheDeadLetteredCounterAndTheFailureKind` — `RecordDeadLettered`
    increments `DeadLettered` + the per-failure-kind breakdown.
  - `SnapshotChanged_FiresOnEachRecord` — the `SnapshotChanged` event fires on each record.
  - `Snapshot_ReflectsMultipleActivityTypes` — the snapshot reflects multiple activity types + the
    failure-kind aggregation (attempts + dead-letters).
  - `DeliveryService_Enqueue_RecordsEnqueuedMetric` — `DeliveryService.DeliverAsync` records the
    `Enqueued` metric (end-to-end through the service, not just the worker).

- **`tests/Iris.Server.Tests/Observability/DeliveryMetricsIntegrationTests.cs`** (new, 3 tests):
  drive a real `DeliveryWorker` (run as a hosted service) against a failable transport and assert the
  `IrisDeliveryMetrics.Snapshot` reflects the outcome:
  - `SuccessfulDelivery_AccruesDelivered` — a 2xx delivery accrues `Delivered` = 1.
  - `TransientFailureThenSuccess_AccruesAttemptFailedAndDelivered` — a 500-then-200 sequence accrues
    `AttemptFailed` = 1 + `Delivered` = 1.
  - `PermanentFailure_AccruesDeadLettered` — three 500s (MaxAttempts = 3) accrues `AttemptFailed` = 3
    + `DeadLettered` = 1 + `ByFailureKind["NonSuccessStatus"]` = 4 (3 attempts + 1 dead-letter).

## Verification

- `dotnet build` 0 warnings; **894/894** green (Iris.Server.Tests 656 → 667, +11 new tests).

## Notes

- **The `ByFailureKind` dictionary aggregates BOTH attempt-failures AND dead-letters.** A job that
  fails 3 attempts and is dead-lettered contributes 4 to `ByFailureKind["NonSuccessStatus"]` (3 from
  `RecordAttemptFailed` + 1 from `RecordDeadLettered`). This is intentional — the failure kind is a
  label for "how many times did this kind of failure happen?", and a dead-letter is the terminal
  event of a failed delivery.
- **The `pending` gauge (queue backlog) is not an instrument here.** It is reported by the
  `DeliveryQueueHealthCheck` (Phase 17.1), which already surfaces the backlog to the health endpoint.
  A host that wants a live gauge can add an OTel gauge that reads `IDeliveryQueue.Count`.
- **The `duration` histogram (wall-clock time of a single attempt) is deferred.** It would need the
  `System.Diagnostics.Metering` API (a `Histogram<double>`), which is not available in this trimmed
  environment. The retry backoff already bounds the time, so the duration is less critical than the
  outcome counters. A follow-up can add it when the `Meter` API is available.
- **OpenTelemetry integration is a host-level concern.** The `IrisDeliveryMetrics` class is
  deliberately OTel-agnostic. A host that wants to export the metrics adds the OpenTelemetry SDK +
  an exporter and either (a) subscribes to `SnapshotChanged` and pushes the `Snapshot` to an OTel
  `Counter`/`Histogram`, or (b) wraps the `IrisDeliveryMetrics` in a custom OTel `Meter` (once the
  `System.Diagnostics.Metering` API is available). The library does not prescribe an exporter.
