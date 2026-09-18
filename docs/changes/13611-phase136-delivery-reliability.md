# 136.11 — Delivery reliability, retries, and dead-letter handling

**Date:** 2026-09-14
**Slice:** 136.11 (Lemmy interop — delivery reliability, retries, and dead-letter handling)
**Status:** **COMPLETE (tests).** The core retry/dead-letter logic in `DeliveryWorker` was already
implemented and well-tested. Three integration tests pin the remaining gaps: idempotency across
retries, end-to-end dead-lettering in a real topology, and `Retry-After` HTTP-date form handling.

## What this slice delivers

136.11's acceptance criteria:

1. Test delivery retry behavior (exponential backoff, max attempts).
2. Test dead-letter handling (what happens when retries are exhausted).
3. Test idempotency (re-delivered activities are not duplicated).
4. Exit when delivery reliability guarantees are pinned by tests.

### Verification: what was already correct (no change)

The delivery reliability behavior was audited end-to-end and found to be **already correct**:

| Behavior | Implementation | Existing coverage |
|----------|---------------|-------------------|
| **Exponential backoff** | `DeliveryWorker.BackoffDelay(attempt)`: `BaseDelay * 2^(attempt-1)`, capped at `MaxDelay` | `DeliveryRetryTests` (unit: backoff values, max attempts, transport error) |
| **4xx = permanent** | `DeliverOneAsync`: 4xx (non-429) → dead-letter immediately, no retry | `CircuitBreakerIntegrationTests` (4xx permanent) |
| **5xx/429 = transient** | `DeliverOneAsync`: 5xx/429 → retry with backoff | `CircuitBreakerIntegrationTests` (429 retried) |
| **Retry-After delay-seconds** | `ParseRetryAfter`: `int.TryParse` → `TimeSpan.FromSeconds` | `CircuitBreakerIntegrationTests` (429 with `Retry-After: 1`) |
| **Dead-letter store** | `DeadLetterAsync`: moves exhausted job to `IDeliveryDeadLetterStore` | `DeadLetterObservabilityTests` (endpoint observability) |
| **Circuit breaker** | `IDeliveryCircuitBreaker`: per-peer failure threshold → open → half-open | `CircuitBreakerIntegrationTests` (circuit open, probe, close) |
| **Dedup by IRI (C-07)** | `InMemoryActivityStore.TryAddActivityAsync`: returns `false` on duplicate IRI | `DuplicateInboundDeliveryIdempotencyIntegrationTests` |

No implementation change was required.

### The genuine gaps (pinned by new tests)

Three gaps were identified and pinned by new integration tests:

**Gap 1: Idempotency across retries in a real topology.** The existing
`DuplicateInboundDeliveryIdempotencyIntegrationTests` tests the dedup mechanism in isolation (direct
inbox POSTs, no federation wiring). The new test verifies that a re-POSTed delivery in a **real
two-instance topology** (with signature validation, the full inbox pipeline, and the
`RoutingFetcher` for key resolution) is stored exactly once.

**Gap 2: End-to-end dead-lettering in a real topology with fast retries.** The existing
`DeliveryDeadLetterIntegrationTests` was **skipped** (`Skip = "hangs >30s"`) due to an
initialization-order bug: the source server (A) was created before the target server (B), so the
`LazyHandler` wrapping B's handler did not have its inner handler ready. The new test fixes the
initialization order (create B first, then A) and uses the default retry budget (5 attempts,
1s/2s/4s/8s backoff ≈ 15s). The previously-skipped test was also unskipped and now passes.

**Gap 3: Retry-After HTTP-date form.** The existing `CircuitBreakerIntegrationTests` only tests the
delay-seconds form (`Retry-After: 1`). The HTTP-date form (e.g.,
`Retry-After: Wed, 21 Oct 2015 07:28:00 GMT`) was untested. The new test verifies that a 429
response with an HTTP-date `Retry-After` header (3s in the future) is honored: the worker waits
until the specified date before retrying (the gap between attempts is ≥ 2000ms, well above the
zero-base-delay backoff that would otherwise fire immediately).

### New tests

**`DeliveryReliabilityIntegrationTests`** (two-instance: A `a.domain.local`/alice, B
`b.domain.local`/bob; `RoutingFetcher` for key resolution, `FailingInboxHandler` for the dead-letter
test):

1. **`RetriedDelivery_IsProcessedExactlyOnce_OnReceivingInstance`** — alice (A) posts a `Create` to
   bob's inbox on B. The delivery is POSTed twice (same IRI — simulating at-least-once delivery).
   B's activity store holds the `Create` exactly once (C-07: `TryAddActivityAsync` dedupes by IRI).
   B's inbox for bob holds the `Create` exactly once (`AddToInboxAsync` is idempotent by IRI).

2. **`FailedCrossInstanceDelivery_IsDeadLettered_RealTopology`** — bob (B) follows alice (A). A's
   `FollowActivityHandler` auto-constructs an `Accept` and enqueues it for delivery to bob's inbox on
   B. The `FailingInboxHandler` returns 500 for that POST. The worker retries (5 attempts, default
   backoff 1s+2s+4s+8s ≈ 15s) and dead-letters the job. The dead-letter store holds the entry with
   the correct inbox (bob's inbox), actor (alice, the auto-`Accept`'s actor), kind
   (`NonSuccessStatus`), detail (`"500"`), and attempt count (5).

3. **`RetryAfter_HttpDateForm_IsHonored`** — a `DeliveryWorker` (constructed with zero-base-delay
   retry options) delivers a `Create` to an inbox served by a `RetryAfterHttpDateHandler` (429 with
   `Retry-After` HTTP-date 3s in the future on the first call, 200 on subsequent). The worker waits
   until the specified date before retrying (the gap between the two attempts is ≥ 2000ms).

### Also unskipped

**`DeliveryDeadLetterIntegrationTests.FailedCrossInstanceDelivery_IsDeadLettered_InRealTopology`**
(pre-existing, 26.5) was skipped with `Skip = "hangs >30s"`. The root cause was the same
initialization-order bug: A was created before B, so the `FailingInboxHandler` wrapping B's handler
did not have its inner handler ready. The test now passes (15s) — the skip was removed.

### Test count

Iris.Server.Tests: 1174 passed (1171 + 3 new), 17 skipped (18 - 1 unskipped), 0 failed.
