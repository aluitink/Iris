# 109 — Phase 16.1: Bounded-concurrency delivery pump

> 2026-08-30 · Phase 16.1 (production persistence & scaling) · `Iris.Server`

## What was built

The `DeliveryWorker` (the background service that pumps `DeliveryJob`s off the `IDeliveryQueue` and
POSTs them to recipients' inboxes) previously delivered jobs **serially** — one in flight at a time.
For a production instance that is a bottleneck: when a burst of outbound deliveries is queued (a
popular local actor posting, a community following many remote actors, a relay fan-out), the worker
delivers them one network round-trip after another, leaving the instance's outbound bandwidth idle.

Phase 16.1 adds **bounded concurrency**: the worker now delivers up to
`DeliveryWorkerOptions.MaxConcurrentDeliveries` jobs in flight at once. A value of 1 (the default)
preserves the original serial behavior; a higher value delivers a burst in parallel, overlapping the
per-delivery network round-trips, while capping the number of concurrent outbound connections the
instance opens (so a burst cannot exhaust the local connection pool or hammer a single remote peer).

## Key types

- **`DeliveryWorkerOptions`** (`src/Iris.Server/Delivery/DeliveryWorkerOptions.cs`) — **new** options
  record with `MaxConcurrentDeliveries` (default 1). A host rebinds it to scale outbound delivery.
- **`DeliveryWorker`** (`src/Iris.Server/Delivery/DeliveryWorker.cs`) — reworked `ExecuteAsync` to a
  bounded-concurrency pump; a new constructor overload takes `maxConcurrentDeliveries` (the existing
  overloads default it to 1, so all existing call sites are unchanged).
- **`ActivityPubServerExtensions`** (`src/Iris.Server/ActivityPubServerExtensions.cs`) — registers
  `DeliveryWorkerOptions` (rebindable) and wires `MaxConcurrentDeliveries` into the hosted worker.

## How the pump works

- A **single dequeuer** pulls one job, acquires one of `MaxConcurrentDeliveries` semaphore slots, and
  starts a delivery task for it; the task releases its slot when it finishes (delivered or
  dead-lettered). At most `MaxConcurrentDeliveries` deliveries are therefore in flight.
- The **dequeue is outside the semaphore-wait**, so a slow in-flight delivery can never stall the
  dequeuer (which would serialize the worker and risk a semaphore deadlock).
- The worker **stops once the queue is complete and empty AND every in-flight delivery has finished**
  (a drain loop using `Task.WhenAny` over the in-flight set). A host shutdown cancels the stopping
  token, which the in-flight deliveries observe on their next await.
- Each in-flight delivery **still honors the per-job F-22 retry / dead-letter policy independently**
  (the existing `DeliverOneAsync` is unchanged).

## Tests

6 new integration tests in `DeliveryWorkerConcurrencyTests` (TestServer-free; drive a real
`DeliveryWorker` hosted service against a delaying stub transport that tracks the peak in-flight count):

- A burst with concurrency > 1 is delivered in parallel (`MaxInFlight > 1`).
- A burst never exceeds `MaxConcurrentDeliveries`.
- The default (concurrency = 1) is serial (`MaxInFlight == 1`).
- The worker drains completely and stops (no semaphore deadlock) on a burst larger than the cap.
- A queue that completes with in-flight jobs still drains all jobs.
- A sub-1 concurrency is clamped to 1 (serial).

Test count: 1032 → 1038 total. Full suite green; all 55 existing delivery tests (retry, dead-letter,
integration) still pass unchanged.

## Decision: concurrency is a cap, not a rate limit

`MaxConcurrentDeliveries` bounds **parallelism** (how many outbound connections are open at once), not
**throughput over time** (there is no per-second rate limit). This is the right knob for "scale a
burst without exhausting the connection pool": it overlaps the fixed per-delivery network latency while
keeping a hard ceiling on concurrent outbound sockets. A host that also needs a *rate* limit (e.g. to
be a polite peer to a remote instance) would layer a separate policy; that is out of scope here. The
default of 1 keeps the existing behavior byte-for-byte for hosts that do not opt in, so this is a pure
capability addition with no behavior change by default.

## Files changed

- `src/Iris.Server/Delivery/DeliveryWorkerOptions.cs` — **new**
- `src/Iris.Server/Delivery/DeliveryWorker.cs` — bounded-concurrency pump + new ctor overload
- `src/Iris.Server/ActivityPubServerExtensions.cs` — DI registration + wiring
- `tests/Iris.Server.Tests/Delivery/DeliveryWorkerConcurrencyTests.cs` — **new** (6 tests)
