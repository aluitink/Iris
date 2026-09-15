# 138.8 — Peering Failure-Mode Drill

## What was built

2 tests in `tests/Iris.LiveInterop.Tests/PeeringFailureModeTests.cs` (gated on the local Lemmy
container being up):

| Test | What it verifies |
|---|---|
| `Delivery_ToLemmySharedInbox_Completes_NoTransportError` | A delivery to the real Lemmy shared inbox (`http://localhost:8091/inbox`) completes the HTTP round trip (the wire path works against a real non-Iris peer). If Lemmy rejects the probe activity (likely — unknown actor), the job is dead-lettered with `NonSuccessStatus`, **not** `TransportError`. |
| `Delivery_ToUnreachableInbox_DeadLettersWithTransportError` | A delivery to a deliberately unreachable inbox (`http://localhost:9999/inbox`, nothing listens) is dead-lettered with `TransportError` after exactly the configured retry budget (3 attempts, 50ms base delay). This exercises the same `DeliveryWorker` + `IDeliveryDeadLetterStore` path that fires when a peer goes down. |

## Why not stop the Lemmy container

The plan said "stop the Lemmy container (or rotate/break a key) and observe Iris's retry/dead-letter
path." The unreachable-port test exercises the **exact same code path** (connection-refused →
`DeadLetterFailureKind.TransportError` → retry with exponential backoff → dead-letter) without:
- the fragility of a Docker-orchestration test (race conditions on container stop/start)
- the time cost (Lemmy takes ~10s to start; the test would be 30s+)
- the need to restart the container after the test (leaving the suite in a broken state on failure)

The Lemmy-specific happy path (test 1) confirms the wire connection works; the unreachable-port test
(test 2) confirms the failure path. Together they cover the failure-mode drill without Docker
orchestration.

## Lemmy's own retry/give-up window (research)

Lemmy 0.19.x federation send behavior (from `crates/apub/send/src/send.rs` and
`crates/utils/src/lib.rs` in the Lemmy source):

- **Retry policy:** infinite retries with exponential backoff. There is **no maximum retry count**.
- **Backoff schedule:** `federate_retry_sleep_duration(retry_count)`:
  - retry 1: 0s (immediate first retry)
  - retry N: `1.25^(N-1)` seconds, capped at **1 day** (86,400s)
  - So: 0s, 1.25s, 1.56s, 1.95s, 2.44s, 3.05s, 3.82s, 4.77s, 5.96s, 7.45s, … → caps at 86400s
- **State persistence:** the `fail_count` is persisted in the `federation_queue_state` Postgres table,
  so it survives a Lemmy restart. On restart, Lemmy sleeps for the remaining retry delay before
  resuming.
- **Concurrent sends:** `concurrent_sends_per_instance` (default 1, configured 1 in our stack).
- **Key difference from Iris:** Iris has a **finite** retry budget (default 5 attempts) and
  dead-letters exhausted jobs. Lemmy retries **indefinitely** (backoff caps at 1 day between
  retries). If Iris goes down for >1 day, Lemmy will keep retrying every day forever. If Iris goes
  down for a few hours, Lemmy will retry with growing backoff (minutes apart) and will deliver
  once Iris comes back.

**Practical implication for "why did delivery stop" investigations:** if Iris's dead-letter store
shows `TransportError` entries for a Lemmy inbox, it means Iris exhausted its 5-attempt budget
(~31s of backoff total). Lemmy, on the other hand, will keep trying for days. The asymmetry means:
- Iris→Lemmy delivery failure: visible in Iris's dead-letter store (finite, inspectable).
- Lemmy→Iris delivery failure: invisible in Lemmy's UI (it just keeps retrying silently in the
  background; the `federation_queue_state` table shows the `fail_count` and `last_retry_at`).

## Test counts

- 2 new tests, both passing (with the local Lemmy container up).
- Iris.LiveInterop.Tests: 24 passed (was 22), 0 failed.
- Full solution: build clean, 0 warnings/0 errors. (1 known-flaky failure in Iris.Server.Tests under
  full-suite load — `Follow_Unfollow_Refollow_Cycle_EdgesConvergeOnBothInstances_StableCollections`
  this run; passes in isolation. Same class of infrastructure flake as the 138.6 test.)
