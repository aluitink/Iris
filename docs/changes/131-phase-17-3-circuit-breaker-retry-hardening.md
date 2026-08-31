# 130 → 131: Phase 17.3 — Circuit Breaker + Retry Hardening

**Date:** 2026-08-31
**Status:** Complete
**Branch:** main

## Summary

Phase 17.3 adds a **per-peer circuit breaker** and **retry hardening** to the outbound delivery worker.

### Circuit Breaker

When a peer's inbox endpoint is down (repeatedly returning 5xx / timing out), the worker would
otherwise retry *every* queued job to that peer with its full `DeliveryRetryOptions` budget — hammering
a downed peer with a stream of doomed requests while the queue backs up. A circuit breaker stops this:

- **Closed → Open:** Once a peer accumulates `FailureThreshold` consecutive failures (across all
  deliveries to that peer), the circuit for that peer **opens**. Deliveries to that peer are **skipped**
  (dead-lettered immediately with `CircuitOpen` failure kind, no network call) until `OpenDuration`
  elapses.
- **Open → Half-open:** After `OpenDuration`, the circuit transitions to **half-open**: a single probe
  delivery is allowed through. If the probe succeeds the circuit **closes** (peer healthy again); if it
  fails the circuit **re-opens** for another `OpenDuration`.
- **Per-peer:** The breaker keys on the **host** of the recipient's inbox IRI (mirroring the Phase 16.3
  rate limiter). All of a peer's inboxes share a single circuit.
- **Disabled by default:** `FailureThreshold = 0` (the default) disables the breaker — the worker
  delivers exactly as before.

### Retry Hardening

1. **4xx = permanent:** A 4xx response (other than 429) is treated as *permanent* — the job is
   dead-lettered immediately without exhausting the retry budget. A bad inbox (404), auth failure (403),
   or malformed activity (400) will not succeed on retry.
2. **429 = transient:** 429 (Too Many Requests) is *not* permanent — it is retried (the server is
   rate-limiting, not rejecting).
3. **Retry-After honored:** When a 429/503 response carries a `Retry-After` header (delay-seconds or
   HTTP-date), the worker uses that delay in place of the exponential backoff when it is longer.

### New Types

| Type | Kind | Purpose |
|---|---|---|
| `DeliveryCircuitBreakerOptions` | options | `FailureThreshold` (0 = disabled), `OpenDuration` (default 60s) |
| `IDeliveryCircuitBreaker` | interface | `TryAcquireAsync`, `RecordSuccessAsync`, `RecordFailureAsync` |
| `PerPeerDeliveryCircuitBreaker` | default impl | Per-host circuit: closed → open → half-open → closed |
| `DeadLetterFailureKind.CircuitOpen` | enum value | New failure kind for circuit-open dead-letters |

### Modified Types

| Type | Change |
|---|---|
| `DeliveryWorker` | New `circuitBreaker` constructor param (11→12 args); `DeliverTrackedAsync` checks the breaker before sending; `DeliverOneAsync` records success/failure on the breaker, distinguishes 4xx (permanent) from 5xx (transient), honors `Retry-After` |
| `DeliverAsAsync` | Returns `(int StatusCode, TimeSpan? RetryAfter)` tuple instead of `int`; new `ParseRetryAfter` helper |
| `ActivityPubServerExtensions` | New `DeliveryCircuitBreakerOptions` registration + `CreateDeliveryCircuitBreaker` helper; `DeliveryWorker` factory now passes the breaker |

### Design Decisions

- **Hand-rolled, no NuGet:** Consistent with Phase 17.1/17.2 (health checks, metrics) and the Phase 16.3
  rate limiter. No `Polly` or `Microsoft.Extensions.Http.Resilience` dependency.
- **Circuit check in `DeliverTrackedAsync`:** The circuit is checked *before* the rate limiter and the
  delivery attempt. When open, the job is dead-lettered immediately (no network call, no rate-limiter
  wait) — this is the "stop hammering a downed peer" behavior.
- **Circuit records in `DeliverOneAsync`:** Success/failure is recorded *per attempt* (not per job), so
  the circuit sees individual network outcomes. A job that fails 3 times and then succeeds records 3
  failures + 1 success (the success resets the count).
- **4xx permanent, 429 transient:** 4xx (except 429) is a client error that will not be fixed by retrying
  (bad inbox IRI, auth failure, malformed activity). 429 is a server-side rate limit that may clear.
- **`Retry-After` honored:** The `Retry-After` header (RFC 9110 §10.2.1) supports two forms: a
  non-negative integer (delay-seconds) and an HTTP-date. The worker parses both; an unparseable value
  falls back to the exponential backoff.

### Tests

17 new tests (12 unit + 5 integration):

**Unit tests** (`CircuitBreakerUnitTests.cs`):
- `DisabledBreaker_AlwaysPermits_NoStateTracking` — threshold 0 is a no-op
- `DisabledBreaker_ThrowsOnNegativeThreshold` — validation
- `EnabledBreaker_ThrowsOnNegativeOpenDuration` — validation
- `ClosedState_FailuresBelowThreshold_StaysClosed` — 2 < 3: stays closed
- `ClosedState_Success_ResetsFailureCount` — success resets the count
- `OpenState_ThresholdFailures_OpensCircuit` — 3 ≥ 3: opens
- `OpenState_IsPerPeer_OtherPeersUnaffected` — Alice open, Bob unaffected
- `HalfOpenState_AfterOpenDuration_AllowsSingleProbe` — openDuration 0: immediate half-open
- `HalfOpenState_SingleProbe_SecondDeliveryDenied` — one probe at a time
- `HalfOpenState_ProbeSuccess_ClosesCircuit` — probe success → closed
- `HalfOpenState_ProbeFailure_ReOpensCircuit` — probe failure → re-open
- `OpenState_BeforeOpenDuration_StaysOpen` — 10-min open: stays open

**Integration tests** (`CircuitBreakerIntegrationTests.cs`):
- `CircuitOpens_AfterThresholdFailures_SubsequentDeliveries_Skipped` — circuit opens, 2nd job dead-lettered with `CircuitOpen` (no network call)
- `CircuitOpen_DoesNotAffectOtherPeers` — per-peer isolation
- `FourXxResponse_DeadLetteredImmediately_NoRetry` — 404 → 1 network call, dead-lettered
- `FourTwentyNine_IsNotPermanent_Retried` — 429 → retried (MaxAttempts=3 → 3 calls)
- `RetryAfterHeader_IsHonored` — 429 + `Retry-After: 1` → worker waits, then retries successfully

**Updated existing tests** (`DeliveryRetryTests.cs`):
- 4 tests updated from `HttpStatusCode.BadRequest` (400, now permanent) to `HttpStatusCode.InternalServerError` (500, still transient)

**Test count:** 894 → 911 (+17 new, 0 removed). All 1136 tests green.

## Files Changed

| File | Change |
|---|---|
| `src/Iris.Server/Delivery/DeliveryCircuitBreakerOptions.cs` | **New** — options (FailureThreshold, OpenDuration) |
| `src/Iris.Server/Delivery/IDeliveryCircuitBreaker.cs` | **New** — interface (TryAcquireAsync, RecordSuccessAsync, RecordFailureAsync) |
| `src/Iris.Server/Delivery/PerPeerDeliveryCircuitBreaker.cs` | **New** — per-host circuit breaker (closed/open/half-open state machine) |
| `src/Iris.Server/Delivery/DeadLetterEntry.cs` | `DeadLetterFailureKind.CircuitOpen = 2` added |
| `src/Iris.Server/Delivery/DeliveryWorker.cs` | Circuit breaker wiring + 4xx permanent + Retry-After |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | DI registration + `CreateDeliveryCircuitBreaker` helper |
| `tests/Iris.Server.Tests/Delivery/CircuitBreakerUnitTests.cs` | **New** — 12 unit tests |
| `tests/Iris.Server.Tests/Delivery/CircuitBreakerIntegrationTests.cs` | **New** — 5 integration tests |
| `tests/Iris.Server.Tests/Delivery/DeliveryRetryTests.cs` | 4 tests: 400 → 500 (permanent → transient) |

## Roadmap

- **Phase 17.1** (health checks + graceful shutdown): ✅ (change 113)
- **Phase 17.2** (structured logging + delivery metrics): ✅ (change 130)
- **Phase 17.3** (circuit breaker + retry hardening): ✅ (this change)
- **Phase 17.4** (inbound rate limiting): ⏳ next
