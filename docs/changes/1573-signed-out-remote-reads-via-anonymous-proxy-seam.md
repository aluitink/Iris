# 1573 — Signed-out remote reads route through the same-origin anonymous proxy seam

**Date:** 2026-09-20
**Closes:** [S2](../qa/s02-signed-out-proxy-bypass.md) (signed-out `/` CORS/blank avatars), [S14](../qa/s14-signed-out-actor-detail-csp.md) (signed-out remote actor-detail CSP)
**Owner design rule (2026-09-20):** *any request for a remote actor or content from the UI should move through the proxy endpoint.*

## Problem

A signed-out visitor's browser **cannot** dial a cross-origin remote instance directly: a direct `GET` to
`https://{remote-host}/...` is CORS-blocked (Mastodon's strict `/ap/users/{id}` surface sends no
`Access-Control-Allow-Origin`) and CSP-blocked (`connect-src 'self'` forbids cross-origin XHR/fetch). So
signed-out remote reads broke in three places, all with the same root cause:

- **S2** — signed-out `/` (home feed): remote actor docs fetched directly → 3× CORS + 3× `ERR_FAILED` console
  errors, 3 blank/fallback avatars for numeric-ID remote authors.
- **S14 (actor doc)** — signed-out `/actor?iri={remote}`: the actor document itself was fetched directly →
  the profile failed to render.
- **S14 (posts tab, residual)** — the actor-detail **Posts** tab (`PagedCollection` reading the remote outbox)
  did a direct cross-origin `GET` of `{remote}/outbox?limit=N` → CSP `connect-src 'self'` violation, "Failed to
  load." error in the tab.

The server already had a signed **POST** proxy endpoint (`POST /ap/v1/proxy/{target}`) that the authenticated
UI used for cross-instance reads. What was missing was an **anonymous** seam: a signed-out visitor has no actor
to sign with, and the browser can't carry Basic auth either, so a cookie-less `GET` to the proxy 401'd.

## Fix

### Server — the anonymous proxy seam

A **cookie-less `GET`** (no Basic auth, no authenticated site cookie) to `GET /ap/v1/proxy/{target}` is now
allowed. It is relayed **only** as an **unsigned public ActivityPub `GET`** (there is no actor to sign with),
checked against the **same** target allowlist policy as the authenticated path, and bounded by a **per-client-IP
rate limit** (the anonymous seam has no actor identity). A non-anonymous `GET` (a site cookie is present) and
every anonymous **write** (`POST` without credentials) still 401 — writes require an actor. The seam is
disabled when `ProxySettings.AllowAnonymousReads = false` (it then 401s).

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `ProxyHandler` gains the anonymous-`GET` gate
  (`isAnonymousRead`); on the anonymous path the outbound client is built with `ActorId = null` (unsigned) and
  no `X-Iris-Actor` header; the rate-limited path sets `Retry-After: 60`. DI registers the singleton limiter; a
  new route `group.MapGet("/proxy/{**target}", ProxyHandler).WithName("proxy-endpoint-anonymous")` mirrors the
  existing POST route.
- `src/Iris.Server/Http/Proxy/AnonymousProxyRateLimiter.cs` (new) — per-client-IP in-memory counter
  (`TryAllow(clientIp, out string? reason)`), 1-minute rolling window, opportunistic pruning, thread-safe.
- `src/Iris.Server/ActivityPubServerOptions.cs` / `ActivityPubServerConstants.cs` —
  `ProxySettings.AllowAnonymousReads` (default `true`) + `AnonymousMaxRequestsPerMinute`
  (default `DefaultAnonymousProxyDecay... = 60`).
- `src/Iris.Client/ActivityPubClientFactory.cs` — `Create` with a **null** `ActorId` now builds an **unsigned**
  pipeline (`JsonLdHandler(httpHandler)`, no `SigningHandler`); a signed write still requires an `ActorId`.

### Client — route every signed-out remote read through the same-origin proxy

- `apps/Iris.Web.Client/Ui/UiContext.cs` — `FetchActorAsync` now routes **any** remote IRI through the proxy:
  signed-in → `FetchViaProxyAsync` (signed `POST`); signed-out → new `FetchViaAnonymousProxyAsync` (a cookie-less
  same-origin `GET /ap/v1/proxy/{target}`). `FetchActorDocumentAnonymousAsync` is now the **local**-actor
  last-resort only (a local actor is public on its own origin, so a direct `GET` is correct there).
- `apps/Iris.Web.Client/Components/PagedCollection.razor` — the signed-out anonymous read path now distinguishes
  **remote** from **local** collections: `IsRemoteCollectionIri` (host ≠ the home instance's origin, from the
  "iris" `HttpClient` base address; treated as remote when undeterminable — the safe default) routes a remote
  page through `FetchAnonymousPageViaProxyAsync` (cookie-less same-origin `GET /ap/v1/proxy/{target}?limit=N`),
  while a local collection keeps dialing directly. The page parsing is shared in `ApplyPageJson`.
- `apps/Iris.Web.Client/Components/Pages/ActorDetail.razor` — signed-out fallback comment/doc updated to the
  S2/S14 seam.

## Why a server-side seam (not client-side CORS)

The browser is the thing that can't reach the remote — no client-side flag changes that. Routing through the
**same-origin** proxy makes the browser's request same-origin (CSP `connect-src 'self'` satisfied, no CORS
pre-flight) and lets the **server** dial the remote with the same allowlist + rate-limit policy it already applies
to authenticated reads. It reuses the existing `ProxyHandler` (target policy, GONE cache, durable remote-actor
archiving, `TryServeCachedTargetAsync`) rather than a parallel anonymous fetch path.

## Tests

- `tests/Iris.Server.Tests/ProxyFallbackIntegrationTests.cs` — 6 new anonymous-seam tests (anonymous actor relay
  200, unsigned relay, remote Note relay + archive, remote actor durable archive, anonymous write 401, allowlist
  403, rate-limit 429) + `ProxyAnonymousGetAsync` helpers; the existing `Proxy_ForwardedGet_IsSignedByActorsKey_NotUnsigned`
  now asserts the disabled-seam 401 + signed 200.
- `tests/Iris.Client.Tests/ActivityPubClientFactoryTests.cs` — `Create_MissingActorId_BuildsUnsignedClient`
  (null `ActorId` builds a valid unsigned client).

## Verification

- Full `dotnet build`: 0 warnings / 0 errors.
- `dotnet test --filter "Category!=Slow"`: **Server 1377 passed / 8 skipped**, Client 190, Web 106, Data 20,
  Sample 38 — 0 failures.
- Live (Playwright, signed out, fresh container):
  - `/` → **0 console errors**; feed loads; every remote actor read goes through same-origin
    `GET /ap/v1/proxy/...` → 200 (aus.social, mastodon.social, hachyderm.io, mstdn.social) — **S2 fixed**.
  - `/actor?iri=https://lemmy.luit.ink/u/lemmyadmin` → profile renders (banner/avatar/handle); the **Posts** tab
    reads `{remote}/outbox?limit=5` via `GET /ap/v1/proxy/{remote}/outbox?limit=5` → 200, **0 console errors**
    (empty outbox → "No posts yet") — **S14 fixed** (both the actor-doc facet and the residual posts-tab CSP facet).
