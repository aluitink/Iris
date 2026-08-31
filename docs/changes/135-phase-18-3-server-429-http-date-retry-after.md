# 135 — Phase 18.3: server-side 429 with HTTP-date Retry-After

## Summary

Phase 17.4 (change 132) added per-peer inbound rate limiting to the server: a peer that exceeds its budget receives `429 Too Many Requests` with a fixed `Retry-After: 60` (delta-seconds) header. Phase 18.1 (change 133) hardened the client's `RetryHandler` to honor both forms of the `Retry-After` header (delta-seconds and HTTP-date). This change enhances the server to send an HTTP-date `Retry-After` (RFC 9110 §10.2.1) instead of a fixed 60-second delta, so the client's `RetryHandler` can back off precisely (the date is when the peer's window resets).

## What changed

### `IInboundRateLimiter` (interface)

- New `GetRetryAfter(string senderHost)` method: returns the `DateTimeOffset` when the peer's rate-limit window will reset (the earliest time the peer may retry). Used to set the `Retry-After` HTTP-date header on 429 responses. When the limiter is disabled, returns the current time (the caller should not send a `Retry-After` header).

### `SlidingWindowInboundRateLimiter` (implementation)

- Implements `GetRetryAfter`: returns the time when the peer's oldest timestamp falls out of the sliding window (the window reset time). When the limiter is disabled or no requests are recorded, returns the current time.
- New `PeerWindow.Oldest()` method: returns the oldest timestamp in the window (the first element of the list).

### `ActivityPubServerExtensions` (server)

- The 429 response now sends an HTTP-date `Retry-After` (RFC 9110 §10.2.1) instead of a fixed 60-second delta. The date is when the peer's window resets (precise, not an estimate). When the window has already expired (race condition), falls back to a 1-second delta.

### Tests

- 5 new unit tests for `GetRetryAfter`:
  - `DisabledLimiter_GetRetryAfter_ReturnsNow`
  - `EnabledLimiter_GetRetryAfter_BeforeAnyRequests_ReturnsNow`
  - `EnabledLimiter_GetRetryAfter_AfterBudgetExhausted_ReturnsFutureTime`
  - `EnabledLimiter_GetRetryAfter_WindowExpired_ReturnsNow`
  - `EnabledLimiter_GetRetryAfter_DifferentPeers_AreIndependent`
- 1 updated integration test: `Inbox_RejectsBeyondBudget` now asserts the 429 carries an HTTP-date `Retry-After` (not a delta-seconds).

## Test results

- 5 new unit tests + 1 updated integration test (1180 total, all passing).
- No changes to the client code (the client already honors both forms, Phase 18.1).

## Files changed

- `src/Iris.Server/Security/IInboundRateLimiter.cs`: added `GetRetryAfter` method.
- `src/Iris.Server/Security/SlidingWindowInboundRateLimiter.cs`: implemented `GetRetryAfter` + `PeerWindow.Oldest()`.
- `src/Iris.Server/ActivityPubServerExtensions.cs`: 429 response now sends HTTP-date `Retry-After`.
- `tests/Iris.Server.Tests/Security/InboundRateLimiterUnitTests.cs`: 5 new tests.
- `tests/Iris.Server.Tests/Security/InboundRateLimitIntegrationTests.cs`: 1 updated test.
