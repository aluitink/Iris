# 111 — Phase 16.3: Per-peer outbound-delivery rate limiting

> 2026-08-30 · Phase 16.3 (production persistence & scaling) · `Iris.Server`

## What was built

The Phase 16.1 bounded-concurrency pump (`DeliveryWorkerOptions.MaxConcurrentDeliveries`) bounds how
many deliveries are **in flight at once** (connection-pool pressure). It does **not** bound how *fast*
the worker may send to a single remote peer over time. A burst of deliveries (a relay fan-out, a popular
actor posting to many followers, or a retry storm to a downed peer) can overwhelm one peer's inbox
endpoint even when the concurrency cap is modest: with the cap at, say, 4 and a 200-delivery burst to a
single peer, the worker hammers that peer's inbox with back-to-back sends for the duration of the burst.

Phase 16.3 adds **per-peer outbound-delivery rate limiting**: the `DeliveryWorker` gates each delivery on
a `SlidingWindowDeliveryRateLimiter` keyed by the **host** of the recipient's inbox IRI, so a peer never
receives more than `PerPeerMaxRequestsPerMinute` deliveries per sliding minute. The limit is keyed by
host (not by full inbox IRI) so all of a peer's inboxes (shared inbox + per-actor inboxes) share a single
outbound rate budget — a well-behaved instance wants one polite rate to a remote peer, not one per inbox.

## Key types

- **`IDeliveryRateLimiter`** (`src/Iris.Server/Delivery/IDeliveryRateLimiter.cs`) — **new** seam.
  `Task WaitUntilPermittedAsync(Iri inboxIri, CancellationToken ct)`: waits until a delivery to the peer
  behind `inboxIri` is permitted, then records it (consuming one slot in the peer's window). Returns
  immediately when disabled or the peer's window has room.
- **`SlidingWindowDeliveryRateLimiter`** (`src/Iris.Server/Delivery/SlidingWindowDeliveryRateLimiter.cs`)
  — **new** default implementation. A per-peer sliding window: records a timestamp for every allowed
  delivery and, when the peer's last `maxRequests` timestamps are all inside the window, waits until the
  oldest falls out. Per-peer state is guarded by a per-peer `SemaphoreSlim` so concurrent delivery tasks
  (up to the Phase 16.1 cap) cannot admit more than `maxRequests` deliveries per window. Constructed with
  `maxRequests == 0` it is a no-op (the disabled default).
- **`DeliveryRateLimitOptions`** (`src/Iris.Server/Delivery/DeliveryRateLimitOptions.cs`) — **new**.
  `PerPeerMaxRequestsPerMinute` (default `0` = disabled). Registered as a singleton in DI.
- **`DeliveryWorker`** — extended with an `IDeliveryRateLimiter?` constructor parameter (default `null` =
  disabled). The gate sits in `DeliverTrackedAsync`, **while holding a concurrency slot**, so a
  rate-limited peer's blocking wait never stalls the single dequeuer — other peers' deliveries still flow
  in parallel. A disabled limiter (null or `maxRequests == 0`) returns immediately, so the default
  behavior is byte-for-byte unchanged.
- **`ActivityPubServerExtensions`** — registers `DeliveryRateLimitOptions` (default disabled) and passes a
  `SlidingWindowDeliveryRateLimiter` into the worker. A host opts in by rebinding `DeliveryRateLimitOptions`
  with `PerPeerMaxRequestsPerMinute > 0`.

## How it works

- **Peer key = inbox host.** `PeerKey` lower-cases the `Uri.Host` of the inbox IRI (e.g.
  `https://b.example/ap/v1/u/bob/inbox` → `b.example`), per RFC 3986 host case-insensitivity. A relative
  IRI (no scheme/host) is keyed by its full value so it still gets a distinct, stable budget.
- **Sliding window.** Each peer holds an ordered list of delivery timestamps. On each
  `WaitUntilPermittedAsync` the peer's state is locked, timestamps older than `now − window` are dropped
  (they no longer count against the budget), and:
  - if the window has room (`count < maxRequests`), the delivery is recorded immediately;
  - otherwise the limiter waits until the oldest timestamp + window (the next to expire), re-checks
    against the fresh clock, drops the now-expired timestamp, and records the delivery.
- **No deadlock with the concurrency pump.** The gate runs inside `DeliverTrackedAsync`, which holds one
  of the `MaxConcurrentDeliveries` slots. The dequeuer therefore never blocks behind a rate-limited
  delivery waiting for a slot that the delivery itself holds — the delivery holds the slot *and* waits for
  the limiter, while other peers' deliveries (up to the cap) keep flowing. The `CancellationToken` (host
  shutdown) cancels the wait promptly.
- **Complements Phase 16.1.** `MaxConcurrentDeliveries` bounds *how many* are in flight at once (the
  connection pool); the rate limiter bounds *how fast* to a given peer over time (peer politeness). The two
  are orthogonal and compose: a host can run 8 concurrent deliveries while still capping each peer to,
  say, 60/min.

## Tests

7 new tests in `DeliveryWorkerRateLimitTests` (a mix of limiter-level and worker-level, mirroring the
Phase 16.1 concurrency tests' `DelayingHandler` / hosted-service pattern):

- **Limiter:** disabled (`maxRequests == 0`) returns immediately — 100 calls complete in well under 1s.
- **Limiter:** a peer is throttled to `maxRequests` per window — the first `maxRequests` calls are
  immediate, the next waits ≈`window`.
- **Limiter:** different peers are rate-limited independently — two peers each make an immediate first
  call; a second call to one peer waits without affecting the other.
- **Limiter:** a peer's budget refills after the window elapses — a call after the window is immediate.
- **Worker:** disabled rate limit (null) delivers a burst unthrottled (all jobs delivered, queue drained).
- **Worker:** enabled rate limit throttles a burst to a single peer — a 4-delivery burst with 2-per-120ms
  takes ≥120ms (proving the gate is wired in and actually waits).
- **Worker:** a rate-limited peer does not deadlock the pump — a burst larger than the per-peer budget
  drains completely and the host stops within the deadline.

Test count: 1047 → 1054 total (full suite green; all 615 existing `Iris.Server.Tests` + the rest of the
suite still pass unchanged).

## Decision: disabled by default (opt-in)

The rate limiter is **disabled** (`PerPeerMaxRequestsPerMinute == 0`) by default: a host that does not
rebind `DeliveryRateLimitOptions` gets the exact same delivery behavior as before Phase 16.3 (deliver as
fast as the concurrency cap allows). A production host that wants to be polite to peers opts in with a
single options rebind. This is a pure capability addition with no behavior change by default, matching the
opt-in pattern of the Phase 16.2 file-backed delivery queue.

The limiter is a **sliding window** (not a fixed window / token bucket) because it is simple, has no
boundary-cliff artifacts (a fixed window can admit 2× the rate straddling a boundary), and is sufficient
for the "don't hammer a peer" goal. A production host that needs a stricter or adaptive policy (e.g.
back-off on `429` from a peer) can replace `IDeliveryRateLimiter` with a custom implementation — the seam
is stable.

## Files changed

- `src/Iris.Server/Delivery/IDeliveryRateLimiter.cs` — **new**
- `src/Iris.Server/Delivery/SlidingWindowDeliveryRateLimiter.cs` — **new**
- `src/Iris.Server/Delivery/DeliveryRateLimitOptions.cs` — **new**
- `src/Iris.Server/Delivery/DeliveryWorker.cs` — `IDeliveryRateLimiter?` constructor param + gate in `DeliverTrackedAsync`
- `src/Iris.Server/ActivityPubServerExtensions.cs` — register `DeliveryRateLimitOptions` + build the limiter in the worker factory
- `tests/Iris.Server.Tests/Delivery/DeliveryWorkerRateLimitTests.cs` — **new** (7 tests)
- `tests/Iris.Server.Tests/Delivery/DeliveryWorkerConcurrencyTests.cs` — pass `null` rate limiter (new ctor param)
