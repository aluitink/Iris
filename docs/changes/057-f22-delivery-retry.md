# 057 — F-22 delivery retry / dead-letter (at-least-once delivery)

> 2026-08-29 · Slice 12.12 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes gap **F-22** (delivery retry / dead-letter): before this slice, the `DeliveryWorker`'s failure
policy was to **log a failed delivery at `Warning` and drop it** (the worker's doc deferred retry to a
"production host"). A transient 5xx on the remote side therefore **silently lost a federation
delivery**. The worker now retries a failed delivery up to a configurable budget with exponential
backoff, and a job that exhausts its budget is moved to a **dead-letter store** (an operator can
inspect and re-drive it) instead of dropped. This gives *at-least-once for failed attempts* — a
delivery is never dropped before its retry budget is exhausted, and a re-delivered activity is a
harmless no-op on the receiver (the inbox pipeline dedupes by the activity's `Id`, C-07).

- **Retry policy.** `DeliveryRetryOptions` (`MaxAttempts`=5 total attempts, `BaseDelay`=1s,
  `MaxDelay`=60s) governs the worker's retry budget. A failed delivery (a non-2xx response or a
  transport exception) is retried with an **exponentially-growing backoff** (`BaseDelay` doubled each
  retry, capped at `MaxDelay`) so a downed peer is not hammered. `MaxAttempts`=1 is fail-fast (no
  retry; the job is dead-lettered after the first failure). A host may rebind the options to tune the
  budget.
- **Attempt tracking.** `DeliveryJob` gained an `Attempts` counter (default 0) + `AfterAttempt()`, so
  the worker can record how many times a job was tried (the dead-letter entry carries the count).
- **Dead-letter store.** `IDeliveryDeadLetterStore` / `InMemoryDeliveryDeadLetterStore` (bounded —
  the most recent `capacity`=1000 entries, the oldest evicted when full; newest-first listing) holds
  `DeadLetterEntry`s: the target inbox, the activity, the signing actor, the attempt count, a
  `DeadLetterFailureKind` (`NonSuccessStatus` with the HTTP status, or `TransportError` with the
  exception message), and the dead-letter timestamp. `DeadLetterEntry.ToJob()` rebuilds the original
  `DeliveryJob` (attempt count reset) for an operator to re-drive. A production host swaps in a
  **persistent** `IDeliveryDeadLetterStore`; the worker depends only on the interface.
- **Worker.** The retry loop lives in `DeliveryWorker.DeliverOneAsync`: on a 2xx the delivery is done
  (no retry); on failure it backs off and retries until the budget is exhausted, then dead-letters the
  job (or logs at `Error` + drops it when no store is configured — preserving the pre-F-22 opt-out for
  hosts that pass no store). A delivery failure **never throws out of the worker** (a bad delivery
  cannot crash the loop). Cancellation (host shutdown) interrupts the backoff delay promptly and does
  not retry / dead-letter.
- **DI.** `DeliveryRetryOptions` and `IDeliveryDeadLetterStore` are registered (both `TryAdd*`, so a
  host may rebind). The worker is registered via an **explicit DI factory** (not
  `AddHostedService<DeliveryWorker>()`) so the retry policy + dead-letter store are injected
  deterministically — the worker has two constructors (a legacy 5-arg one that defaults to no retry
  store and the default policy, used by existing tests) and an explicit factory avoids relying on
  most-constructible-overload selection.

*Scope note:* this is **in-memory retry + dead-letter** (no persistent queue), matching the decision
recorded for F-22. The retry budget and backoff are per-attempt in-memory state; a restart clears the
in-flight queue (as before) and the in-memory dead-letter store. A production host that needs
restart-surviving dead-letters swaps in a persistent `IDeliveryDeadLetterStore` (the seam is
interface-only). The "at-least-once" guarantee is for *failed* attempts: a delivery that returns 2xx
but whose acknowledgement is lost is not detected (the worker treats 2xx as delivered).

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/DeliveryRetryOptions.cs` | The retry policy (`MaxAttempts`=5, `BaseDelay`=1s, `MaxDelay`=60s). |
| `src/Iris.Server/DeliveryJob.cs` | `Attempts` counter + `AfterAttempt()` (the job now tracks how many times it has been tried). |
| `src/Iris.Server/DeadLetterEntry.cs` | `DeadLetterEntry` record + `DeadLetterFailureKind` enum + `ToJob()` (re-drive). |
| `src/Iris.Server/IDeliveryDeadLetterStore.cs` | The dead-letter seam (`AddAsync`, `Count`, `ListAsync`). |
| `src/Iris.Server/InMemoryDeliveryDeadLetterStore.cs` | The default in-memory bounded store (newest-first, oldest evicted beyond `capacity`). |
| `src/Iris.Server/DeliveryWorker.cs` | The retry loop (`DeliverOneAsync`): exponential backoff, dead-letter on exhaustion; a 7-arg constructor (retry options + dead-letter store) + a legacy 5-arg overload; `BackoffDelay` + `DeadLetterAsync` helpers. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | DI: `DeliveryRetryOptions` + `IDeliveryDeadLetterStore` registration; the worker registered via an explicit factory. |
| `tests/Iris.Server.Tests/DeliveryRetryTests.cs` | 8 new (retry-on-success, retry-until-success, exhaust→dead-letter [status + transport], fail-fast, no-store drop, store eviction, backoff curve). |

## Tests

722 → **730** (+8):

- `tests/Iris.Server.Tests/DeliveryRetryTests.cs` — 8 new. Each drives a real `DeliveryWorker` (run as
  a hosted service) against a **failable transport** (a stub `HttpMessageHandler` that returns a
  scripted sequence of statuses — or throws — and counts each send), with a small retry budget and a
  zero backoff delay (no real waiting). Coverage:
  - a successful (2xx) delivery is delivered on the first attempt (no retry, no dead-letter);
  - a transient failure is retried until it succeeds (3 attempts: 400, 400, 200 → delivered, not
    dead-lettered);
  - a permanent failure exhausts the budget and is dead-lettered with the correct attempt count,
    `NonSuccessStatus` kind, and the last status code;
  - a transport error (the handler throws a non-`HttpRequestException`) is dead-lettered as
    `TransportError` with the exception message;
  - `MaxAttempts`=1 is fail-fast (one attempt, no retry) but still dead-letters;
  - without a dead-letter store the exhausted job is dropped (logged at `Error`), the worker does not
    crash, and the queue drains;
  - the dead-letter store evicts the oldest entry beyond its `capacity` (newest-first listing); and
  - the backoff delay grows exponentially (`100ms, 200ms, 400ms`) and is capped at `MaxDelay`
    (unit-tested via the worker's private `BackoffDelay` through reflection).

The `RetryHandler` (the client's own retry layer) does not interfere: a delivery is a **POST**
(non-idempotent), which the `RetryHandler` passes straight through without retrying, so the
`DeliveryWorker`'s retry is the only retry in play and the transport handler is called exactly once
per worker attempt.

## Decisions

- **In-memory retry + dead-letter (no persistent queue).** Confirmed for F-22: the retry state
  (attempt count, backoff) and the dead-letter store are in-memory. A production host that needs
  restart-surviving dead-letters swaps in a persistent `IDeliveryDeadLetterStore` (interface-only
  seam) — the worker and its retry logic are unchanged. This keeps the default dependency-free and
  testable while leaving the production path open.

- **At-least-once for failed attempts, not exactly-once.** A delivery is never dropped before its
  retry budget is exhausted (at-least-once for the *failure* path). A 2xx is treated as delivered; a
  lost acknowledgement is not detected. A re-delivered activity is a harmless no-op on the receiver —
  the inbox pipeline dedupes by the activity's `Id` (C-07) — so retrying is safe (no duplicate
  side-effects).

- **Exponential backoff, capped, on a cancellable delay.** `BaseDelay` doubled each retry (capped at
  `MaxDelay`) so a downed peer is not hammered, and the wait is a `Task.Delay(…, ct)` so a host
  shutdown interrupts it promptly (no long hang on stop).

- **No dead-letter store ⇒ drop + log at `Error` (pre-F-22 opt-out).** A host that constructs the
  worker without a store (the legacy 5-arg path) gets the old behavior — an exhausted job is logged
  at `Error` and dropped — so adopting F-22 is opt-in via the DI registration, not a forced
  behavior change.

- **The worker is registered via an explicit DI factory.** The worker has two constructors; an
  explicit `AddHostedService(sp => new DeliveryWorker(…))` injects the retry policy + dead-letter
  store deterministically rather than relying on DI's most-constructible-overload selection.

- **`MaxAttempts`=1 is fail-fast but still dead-letters.** `MaxAttempts` is the *total* attempt
  count (not the retry count), so `MaxAttempts`=1 means one attempt with no retry — but a failure
  still dead-letters (the budget is "exhausted" after the first failure). This keeps the semantics
  uniform: any exhausted budget is surfaced, whether or not a retry was attempted.
