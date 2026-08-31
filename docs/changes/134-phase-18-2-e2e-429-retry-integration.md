# 134 — Phase 18.2: end-to-end 429 → retry integration test

## Summary

Phase 17.4 (change 132) added per-peer inbound rate limiting to the server: a peer that exceeds its budget receives `429 Too Many Requests` with a `Retry-After: 60` header. Phase 18.1 (change 133) hardened the client's `RetryHandler` to honor both forms of the `Retry-After` header (delta-seconds and HTTP-date). This change closes the loop: an end-to-end integration test proving the server's 429 → client retry loop works together over a genuine HTTP stack.

## What changed

### `FakeActivityPubServer` (tests)

- New `rateLimitAfter` parameter: when > 0, the first N requests are served normally and subsequent requests return 429 with `Retry-After: 1` (delta-seconds form).
- New `rateLimitResumeAfter` parameter: when > 0, the rate-limit gate resumes serving normally after that many 429 responses (simulating the rate-limit window expiring). When 0 (default), the gate 429s all requests after the first N (the window never expires during the test).
- New `RateLimitGate` class: tracks a request counter and a 429 counter; returns true when the request should be 429'd.

### `ClientServerIntegrationTests` (tests)

- New test `GetActor_Server429WithRetryAfter_ClientHonorsHeader`:
  1. Starts a rate-limiting fake server (`rateLimitAfter: 1`, `rateLimitResumeAfter: 1`).
  2. Makes a probe request (consumes the "served" slot).
  3. The client's first request hits the 429 + `Retry-After: 1`.
  4. The `RetryHandler` honors the header and retries.
  5. The client's second request is served (the rate-limit window expired).
  6. Asserts the actor was fetched and the total hit count is 3 (1 probe + 2 client).

## Test results

- 1 new test (1175 total, all passing).
- No changes to production code.

## Files changed

- `tests/Iris.Client.Tests/FakeActivityPubServer.cs`: added `RateLimitGate` class + `rateLimitAfter`/`rateLimitResumeAfter` parameters to `Start`.
- `tests/Iris.Client.Tests/ClientServerIntegrationTests.cs`: added `GetActor_Server429WithRetryAfter_ClientHonorsHeader` test.
