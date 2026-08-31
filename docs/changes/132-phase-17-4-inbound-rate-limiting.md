# 131 → 132: Phase 17.4 — Per-Peer Inbound Rate Limiting

**Date:** 2026-08-31
**Status:** Complete
**Branch:** main

## Summary

Phase 17.4 adds a **per-peer inbound rate limiter** to the ActivityPub server. A host may rebind
`InboundRateLimitOptions` (`PerPeerMaxRequestsPerMinute > 0`) to bound how many signed inbox POSTs
the server accepts from a single peer (keyed by the host of the signer's `keyId`) per sliding minute.
A peer that exceeds its budget receives `429 Too Many Requests` (fail-fast, not queued).

### Inbound Rate Limiting

Without a rate limit, a malicious or buggy remote instance can flood the inbox with signed
deliveries, exhausting server resources (CPU, memory, thread pool). The inbound limiter bounds this:

- **Per-peer:** The limiter keys on the **host** of the signer's `keyId` (e.g.
  `https://remote.example.org/u/bob#key-1` → `remote.example.org`). All of a peer's actors/keys share
  a single inbound rate budget.
- **Sliding window:** The limiter records a timestamp for every accepted request. When the peer's
  last `maxRequests` timestamps are all inside the window (default one minute), further requests are
  rejected.
- **Fail-fast, not blocking:** Unlike the outbound `SlidingWindowDeliveryRateLimiter` (which blocks a
  background worker until a slot frees), the inbound limiter **rejects** immediately with
  `429 Too Many Requests`. A web request handler that blocks under load risks thread-pool exhaustion
  and request timeouts; a 429 lets the client back off and retry later (the client's `RetryHandler`
  honors `Retry-After` on 429s).
- **Disabled by default:** `PerPeerMaxRequestsPerMinute = 0` (the default) disables the limiter — the
  server accepts inbox POSTs exactly as before.

### New Types

| Type | Kind | Purpose |
|---|---|---|
| `InboundRateLimitOptions` | options | `PerPeerMaxRequestsPerMinute` (0 = disabled) |
| `IInboundRateLimiter` | interface | `TryAcquire(senderHost, ct)` → `bool` (permit/deny) |
| `SlidingWindowInboundRateLimiter` | default impl | Per-host sliding-window limiter (fail-fast) |

### Modified Types

| Type | Change |
|---|---|
| `ActivityPubServerExtensions` | New `InboundRateLimitOptions` registration + `CreateInboundRateLimiter` helper; `InboxHandler` + `CommunityInboxHandler` now receive `IInboundRateLimiter`; `HandleInboxPostAsync` checks the limiter after signature validation |

### Design Decisions

- **Hand-rolled, no NuGet:** Consistent with Phase 16.3 (outbound rate limiter), 17.1 (health checks),
  17.2 (metrics), and 17.3 (circuit breaker). No `Polly` or `Microsoft.Extensions.Http.Resilience`
  dependency.
- **Keyed by host of `keyId`:** The sender's identity is the host of their `keyId` in the HTTP
  signature. This mirrors the outbound limiter (keyed by recipient inbox host) and ensures all of a
  peer's actors/keys share a single budget.
- **Fail-fast, not blocking:** A 429 response (with `Retry-After: 60`) is the correct behavior for
  inbound requests. The client backs off and retries later. Blocking the request handler until a slot
  frees would risk thread-pool exhaustion under load.
- **Checked after signature validation, before body read:** The rate limit is checked *after* the
  signature is validated (only signed requests count) and *before* the body is read (a rejected request
  does not consume body-read resources).
- **Case-insensitive host:** The host is lowercased before use as the key, so `Remote.Example.ORG` and
  `remote.example.org` share the same budget.

### Tests

15 new tests (10 unit + 5 integration):

**Unit tests** (`InboundRateLimiterUnitTests.cs`):
- `DisabledLimiter_AlwaysPermits` — 0 = no-op, 100 requests all permitted
- `DisabledLimiter_DoesNotTrackState` — 100 requests, still permitted
- `Limiter_ThrowsOnNegativeMaxRequests` — validation
- `Limiter_ThrowsOnNonPositiveWindow_WhenEnabled` — validation
- `Limiter_ThrowsOnNullOrEmptyHost` — null → `ArgumentNullException`, empty/whitespace → `ArgumentException`
- `EnabledLimiter_PermitsUpToMaxRequests` — 5 of 5 permitted, 6th rejected
- `EnabledLimiter_RejectsBeyondMaxRequests` — 3 of 3 permitted, 4th + 5th rejected
- `EnabledLimiter_DifferentPeers_AreIndependent` — per-peer isolation
- `EnabledLimiter_HostIsCaseInsensitive` — same host different case shares budget
- `EnabledLimiter_WindowExpires_AllowsNewRequests` — 100ms window, wait 150ms, new request permitted

**Integration tests** (`InboundRateLimitIntegrationTests.cs`):
- `Inbox_PermitsWithinBudget` — 3 signed POSTs all 202
- `Inbox_RejectsBeyondBudget` — 4th POST → 429 + `Retry-After` header
- `Inbox_RejectedRequest_NotProcessed` — 429'd POST is not dispatched (note not stored)
- `Inbox_PerPeerIsolation` — peer A exhausted → 429, peer B → 202
- `Inbox_DisabledLimiter_PermitsAll` — 10 signed POSTs all 202 (limiter disabled)

**Test count:** 1155 → 1170 (+15 new, 0 removed). All 1170 tests green.

## Files Changed

| File | Change |
|---|---|
| `src/Iris.Server/Security/InboundRateLimitOptions.cs` | **New** — options (`PerPeerMaxRequestsPerMinute`) |
| `src/Iris.Server/Security/IInboundRateLimiter.cs` | **New** — interface (`TryAcquire`) |
| `src/Iris.Server/Security/SlidingWindowInboundRateLimiter.cs` | **New** — per-host sliding-window limiter |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | DI registration + `CreateInboundRateLimiter` helper; `HandleInboxPostAsync` rate-limit check |
| `tests/Iris.Server.Tests/Security/InboundRateLimiterUnitTests.cs` | **New** — 10 unit tests |
| `tests/Iris.Server.Tests/Security/InboundRateLimitIntegrationTests.cs` | **New** — 5 integration tests |

## Roadmap

- **Phase 17.1** (health checks + graceful shutdown): ✅ (change 113)
- **Phase 17.2** (structured logging + delivery metrics): ✅ (change 130)
- **Phase 17.3** (circuit breaker + retry hardening): ✅ (change 131)
- **Phase 17.4** (inbound rate limiting): ✅ (this change)
