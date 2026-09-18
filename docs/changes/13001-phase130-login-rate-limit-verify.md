# 130.1 — Login rate limiting: live UI verification

**Date:** 2026-09-13
**Type:** Verification (no code changes)
**Scope:** Confirm the login rate limiter triggers and displays the lockout message in the live app

## Context

The feature matrix listed login rate limiting as ☐ on the D (polish) column. The server-side `SlidingWindowLoginRateLimiter` (5 failed attempts / 15 min window, keyed by `username + remote IP`) was implemented in Phase 52.3 but never exercised via the live UI.

## Verification method

1. Restarted the Docker container (the rate limiter is in-memory — restart clears the window)
2. Navigated to `/login` via Playwright
3. Submitted 5 failed login attempts (correct username, wrong password)
4. Observed the redirect + error message

## Results

| Check | Result |
|---|---|
| 5 failed attempts recorded | ✅ (server-side, keyed by `alice\|<ip>`) |
| 6th attempt rejected | ✅ Redirect to `/login?error=Too+many+failed+attempts...` |
| Error message displayed | ✅ `<p class="error" role="alert">Too many failed attempts. Please try again later. Try again in about 15 minutes.</p>` |
| Message includes retry hint | ✅ "Try again in about 15 minutes." (computed from `RetryAfter`) |
| No info leak | ✅ Same generic message for unknown username and wrong password |

## Notes

- The rate limiter is in-memory (a `ConcurrentDictionary` in `SlidingWindowLoginRateLimiter`). It resets on container restart. In a multi-replica deployment, each replica has its own window (acceptable for the current single-replica deployment).
- The threshold is configurable via `IRIS_LOGIN_MAX_ATTEMPTS` (default 5) and `IRIS_LOGIN_WINDOW_MINUTES` (default 15).
- The `Retry-After` hint is computed from the oldest failure timestamp + window length.

## Conclusion

Feature matrix D-column for "Login rate limiting" can be closed (☐ → ✅).
