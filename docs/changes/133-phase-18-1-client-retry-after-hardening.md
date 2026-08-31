# 132 → 133: Phase 18.1 — Client Retry-After HTTP-Date Hardening

**Date:** 2026-08-31
**Status:** Complete
**Branch:** main

## Summary

Phase 18.1 extends the client's `RetryHandler` to honor the **HTTP-date form** of the `Retry-After`
header (RFC 9110 §10.2.1), not just the delta-seconds form. The server (Phase 17.3) sends `Retry-After`
on 429/503 responses in either form; the client now honors both so a peer that rate-limits with an
HTTP-date is not under-throttled.

### The Gap

Phase 17.3 made the server honor and send `Retry-After` on 429/503 responses. The header supports two
forms (RFC 9110 §10.2.1):

- **delta-seconds:** a non-negative integer (e.g. `Retry-After: 120`)
- **HTTP-date:** an HTTP-date (e.g. `Retry-After: Wed, 21 Oct 2026 07:28:00 GMT`)

The server's `ParseRetryAfter` (Phase 17.3) parses both forms. But the client's `RetryHandler.GetDelay`
only checked `response.Headers.RetryAfter?.Delta` — the delta-seconds form. When a peer sent an
HTTP-date, `Delta` was null, and the handler fell back to the exponential backoff (250ms base) instead
of the server-specified delay. This meant a peer that rate-limited with `Retry-After: <future-date>`
was under-throttled (the client retried too soon).

### The Fix

`RetryHandler.GetDelay` now checks both forms:

1. **Delta first:** If `RetryAfter.Delta` is present and positive, use it (existing behavior).
2. **Date second:** If `RetryAfter.Date` is present and in the future, use `date - UtcNow`.
3. **Fallback:** Otherwise (no `Retry-After`, or a past date), fall back to the exponential backoff.

### Design Decisions

- **Delta preferred over Date:** Per RFC 9110 §10.2.1, a `Retry-After` header carries *either* a
  delta-seconds or an HTTP-date, not both. `RetryConditionHeaderValue` exposes both as nullable
  properties; the delta form is simpler (no clock skew) and is checked first. A real-world peer sends
  one or the other, so the preference is a defensive measure, not a behavior change.
- **Past date → fallback:** A date in the past would produce a negative delay, which is nonsensical.
  The handler falls back to the exponential backoff (the conservative choice: wait a bit rather than
  retry immediately).
- **No change to retry policy:** The set of retryable status codes (429, 5xx), the max-attempt budget,
  and the idempotency gate (GET/HEAD/OPTIONS only) are unchanged. This is a delay-computation fix, not
  a policy change.

### Tests

4 new tests (`HandlerTests.cs`, `RetryHandlerTests`):

- `Get_RetriesOn429_HonorsRetryAfterHttpDate` — 429 + `Retry-After: <now+5s>` → delay ≈ 5s (range
  4.5–5.5s to allow clock skew)
- `Get_RetriesOn503_HonorsRetryAfterHttpDate` — 503 + `Retry-After: <now+3s>` → delay ≈ 3s
- `Get_RetryAfterDateInPast_FallsBackToBackoff` — 429 + `Retry-After: <now-10s>` → falls back to
  exponential backoff (250–500ms range)
- `Get_RetryAfterDeltaPreferredOverDate` — 429 + `Retry-After: 2` (delta) → delay = 2s (delta
  precedence)

Also added `ScriptedHandler.StatusWithRetryAfterDate` helper (sets `Retry-After` to an HTTP-date form).

**Test count:** 1170 → 1174 (+4 new, 0 removed). All 1174 tests green.

## Files Changed

| File | Change |
|---|---|
| `src/Iris.Client/Pipeline/RetryHandler.cs` | `GetDelay` now checks `RetryAfter.Date` when `RetryAfter.Delta` is absent; a past date falls back to the backoff |
| `tests/Iris.Client.Tests/Pipeline/HandlerTests.cs` | 4 new tests + `ScriptedHandler.StatusWithRetryAfterDate` helper |

## Roadmap

- **Phase 17** (observability and transport hardening): ✅ complete (changes 113, 130–132)
- **Phase 18** (client/server hardening): **18.1 done** (this change)
