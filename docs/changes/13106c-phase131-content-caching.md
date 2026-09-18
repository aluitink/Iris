# 131.6 — Content caching policies (development cache-bypass)

**Date:** 2026-09-13
**Slice:** 131.6 (user request — "our browser gets stuck with a cached version of the website often. Investigate a method to create a shorter cache lifetime and/or make it configurable so while we develop we can disable browser content caching to ensure a refresh actually fetches the new content.")
**Commit:** `feat(web): dev cache-bypass so a redeploy never serves a stale build`

## Problem

After each `iris-web` container rebuild/recreate, the browser kept serving the **previous** build:

- **CSS/JS** live at fixed, non-fingerprinted paths (`/css/app.css`, `/js/*.js`) and were served with
  `Cache-Control: public, max-age=86400` — so the browser used the stale copy for up to 24 h.
- **The SPA shell** (`index.html`, served for `/` and every non-API route) had **no** `Cache-Control`
  header, so the browser applied heuristic caching and likewise kept the old shell.
- The shell references the `_framework/` WASM assets by URL; with a stale shell, the browser loads the
  **old** (still-cached-by-URL) WASM build even though a fresh one is deployed.

Net effect: "I rebuilt, but the site still looks/behaves like the old version" — until a hard refresh
or cookie/storage clear.

## Change

A new **development cache-bypass** toggle, `Iris:Dev:CacheBypass` (env `IRIS_DEV_CACHEBYPASS`):

| Asset | Path shape | Dev mode (bypass ON) | Prod mode (bypass OFF, the C# default) |
|---|---|---|---|
| WASM `_framework/` | content-hashed per build | `max-age=31536000, immutable` | `max-age=31536000, immutable` |
| CSS / JS | fixed | `no-cache` (revalidate every request) | `public, max-age=86400` |
| SPA shell `index.html` | fixed | `no-store` | *(no header — pre-131.6)* |

Key design points:

- **`_framework/` is cached long-term in BOTH modes.** Its URLs are content-hashed per build, so a
  redeploy changes the URLs and aggressive caching is safe — this is the standard Blazor WASM
  strategy and is unchanged.
- **The fixed-path assets (CSS/JS + the shell) are the ones that go stale.** In dev mode they are
  `no-cache`/`no-store` so a redeploy's new build is picked up immediately; in prod mode the original
  headers are preserved.
- **The C# production default is OFF** (`IsDevCacheBypassEnabled` fails closed to "cache normally"),
  so a misconfiguration can never accidentally serve an uncached, slower site to public users. The
  **dev compose stack defaults it to ON** (mirroring the Phase 131.5 antiforgery dev-default pattern);
  set `IRIS_DEV_CACHEBYPASS=false` to restore normal caching in the dev stack.
- **The SPA fallback is now an explicit `MapFallback` endpoint** (instead of `MapFallbackToFile`) so
  the shell's `Cache-Control` can be set unambiguously alongside the body. It reads
  `index.html` from `IWebHostEnvironment.WebRootPath` and returns it as `text/html`.

### Why `no-cache`/`no-store` for the shell, not a shorter `max-age`?

A shorter `max-age` (e.g. 60 s) still leaves a window where a redeploy's new build is not seen until
the TTL lapses, and requires the user to wait or hard-refresh. `no-cache`/`no-store` removes the
stale window entirely for the assets that actually change per build — which is exactly the dev pain the
request describes. The cost (a revalidation / re-fetch of a small HTML + CSS/JS per navigation) is
negligible in development and is opt-out in production.

## New / changed API

- `WebAppFactory.DevCacheBypassConfigKey` (`public const` = `"Iris:Dev:CacheBypass"`).
- `WebAppFactory.IsDevCacheBypassEnabled(IConfiguration)` (`internal static`) — `true` only when the
  bound value parses to `true` (case-insensitive); unset/blank/any non-`true` → `false` (cache
  normally).

## Verification (live, Playwright)

**Dev mode (compose default, `IRIS_DEV_CACHEBYPASS` unset → `true`):**
- `GET /home` (SPA shell) → `Cache-Control: no-store`
- `GET /css/app.css`, `GET /js/WebCrypto.js` → `Cache-Control: no-cache`
- `GET /_framework/blazor.webassembly.js` → `Cache-Control: public, max-age=31536000, immutable`
- `GET /ap/v1/health` (API JSON) → no `Cache-Control` (untouched, as before)
- Login as `andrew` → `/home`, 0 console errors.

**Prod mode (one-off container with `IRIS_DEV_CACHEBYPASS=false`):**
- `GET /home` → no `Cache-Control` (pre-131.6 behavior)
- `GET /css/app.css` → `Cache-Control: public, max-age=86400`
- `GET /_framework/blazor.webassembly.js` → `Cache-Control: public, max-age=31536000, immutable`

**Tests:** `dotnet test Iris.slnx` — all 1960 tests pass (0 failures) under full-suite concurrency.
(Also lengthened the flaky `DuplicateInboundDeliveryIdempotencyIntegrationTests` settle budget 15 s →
30 s; it flaked once at 15 s under load — see [13106](13106-phase131-flaky-federation-tests.md).)

## Out of scope

- Content-hashing the CSS/JS filenames (a larger build-pipeline change; the dev cache-bypass makes it
  unnecessary for the stated dev pain).
- A per-request "bypass cache" query param or a runtime toggle endpoint (the env/config toggle covers
  the development workflow).
