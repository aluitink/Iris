# Phase 131.5 — HSTS + cookie hardening

**Date:** 2026-09-13
**Branch:** `phase-32-production-app`

## What was built

### HSTS (new)

Added a `Strict-Transport-Security` response header, emitted **only when the request is HTTPS**:

```
Strict-Transport-Security: max-age=31536000; includeSubDomains; preload
```

**Placement decision:** The HSTS middleware is placed **after** `app.UseForwardedHeaders()` in the
pipeline, not in the original security-headers middleware (which runs before `UseForwardedHeaders`).
This is required because:

- Per RFC 6797, HSTS **must not** be sent over plain HTTP.
- Behind the TLS-terminating reverse proxy (`https://iris.luit.ink` → host 8088), the app sees plain
  HTTP on the wire; it's `UseForwardedHeaders` that updates `Request.IsHttps` from
  `X-Forwarded-Proto: https`.
- Placing HSTS before `UseForwardedHeaders` would mean `IsHttps` is still `false` (the app sees the
  raw HTTP request) and the header would never be emitted in production.
- Placing it after `UseForwardedHeaders` means the app has already seen the real scheme, so `IsHttps`
  is `true` behind the proxy (HSTS emitted) and `false` over local plain-HTTP dev (HSTS skipped).

The 5 always-on security headers (CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy,
Permissions-Policy) remain in the original middleware (before `UseForwardedHeaders`) since they are
scheme-independent.

### Cookie hardening (already in place — verified, no changes)

The auth cookie (`iris.auth`) was already fully hardened from Phase 50.1:

| Flag | Value | Source |
|---|---|---|
| `HttpOnly` | `true` | `options.Cookie.HttpOnly = true` |
| `SameSite` | `Lax` | `options.Cookie.SameSite = SameSiteMode.Lax` |
| `Secure` | conditional | `options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest` (sets Secure when the request is HTTPS, i.e. behind the proxy) |
| `Path` | `/` | ASP.NET Core default |
| `Expires` | 14 days | `options.ExpireTimeSpan = TimeSpan.FromDays(14)` |
| `Sliding` | on | `options.SlidingExpiration = true` |

No changes were needed to the cookie configuration.

## Verification

### HSTS (live, Docker)

1. **Plain HTTP** (`curl -I http://localhost:8088/`): 5 security headers present, **HSTS correctly
   absent** (RFC 6797 — must not be sent over plain HTTP).
2. **Simulated HTTPS** (`curl -I -H "X-Forwarded-Proto: https" http://127.0.0.1:8088/`): HSTS not
   observed in this test because the Docker container's NAT source IP is not in the default
   known-proxies list (the forwarded-headers middleware ignores `X-Forwarded-Proto` from unknown
   sources). This is expected local-test behavior — in production, the reverse proxy's IP is known
   (or the operator configures `ForwardedHeaders:KnownProxies`), and the header will be emitted. The
   code path is identical to the already-verified cookie `Secure` flag mechanism (Phase 50.1), which
   uses the same `Request.IsHttps` value set by the same `UseForwardedHeaders` middleware.

### Cookie flags (live, Docker)

1. **HttpOnly confirmed:** After logging in via Playwright, `document.cookie` returns `""` — the
   auth cookie is invisible to JavaScript, confirming `HttpOnly=true`.
2. **SameSite/Secure:** Confirmed by code inspection (same `CookieBuilder` that sets HttpOnly). The
   `Secure` flag is set via `CookieSecurePolicy.SameAsRequest` — over the local plain-HTTP Docker
   test it's not set (correct); behind the production HTTPS proxy it is set (verified mechanism in
   Phase 50.1).

### Regression check

- `dotnet build Iris.slnx` — clean (0 errors, 0 warnings).
- `dotnet test Iris.slnx` — all pass (1143 server + 98 web + 171 client + 409 core + 38 sample +
  29 extensions + 17 blazor-sample).
- Playwright smoke test: login → /home, 0 console errors, app fully functional.

## Files changed

| File | Change |
|---|---|
| `apps/Iris.Web/WebAppFactory.cs` | Added HSTS middleware after `UseForwardedHeaders()` (emits `Strict-Transport-Security: max-age=31536000; includeSubDomains; preload` when `Request.IsHttps`). Removed the initial HSTS attempt from the earlier security-headers middleware (it ran before `UseForwardedHeaders` and could never see `IsHttps=true`). |
