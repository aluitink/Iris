# S2 — Signed-out remote reads bypass the proxy (CORS/blank avatars)

- **Class:** bug (console-noise + broken avatars) — **Severity:** S2
- **Status:** open (re-confirmed Pass 32, 2026-09-20 — the S2/S14 anonymous-proxy seam is live but the unsigned GET 401s)
- **Found:** Pass 11 (2026-09-20) — re-confirmed Passes 15, 20 (signed-out `/`), 23, 24, 25, 27, 32
- **Related:** [S14](s14-signed-out-actor-detail-csp.md) (same root, actor-detail facet)

## Symptom

Signed-out `/` (home feed) logs **6 console errors (3× CORS + 3× `ERR_FAILED`)** and shows **3 blank/fallback avatars** for numeric-ID remote authors (`mastodon.social/ap/users/117294272768263207`, `…/117294272768263207`→`117300166312075407`, `hachyderm.io/ap/users/117265166278633915`).

## Root cause

`UiContext.FetchActorAsync` (`apps/Iris.Web/.../UiContext.cs:489`) skips the proxy when signed out (`_session.Client is null`) and falls to `FetchActorDocumentAnonymousAsync` (`:611`), a bare `GET` with no ActivityPub `Accept`.

- `https://{host}/users/{username}` answers with `Access-Control-Allow-Origin: *` (200) — works.
- `https://{host}/ap/users/{numeric-id}` (Mastodon's strict ActivityPub surface) sends **no** `Access-Control-Allow-Origin` → the browser CORS-blocks it.

Confirmed live: `mastodon.social/users/deadline` = 401 **with** `ACAO:*`; `mastodon.social/ap/users/117294272768263207` = 401 **without** it. The public feed carries 16 `/ap/users/{id}/statuses/` objects; content is inlined so only the *actor* docs fail.

## Fix

**Design rule (owner, 2026-09-20): any request for a remote actor or content from the UI should move through the proxy endpoint.**

Add a **public/anonymous GET proxy path** — the server already resolves these numerics fine when signed (`ProxyHandler` + `TryServeCachedTargetAsync`) — so signed-out visitors route remote actor/content reads through the same-origin proxy instead of direct fetches. The proxy's hard `401 if authenticatedHandle is null` gate (`ActivityPubServerExtensions.cs:1844-1846`) must allow unsigned GET reads of public remote content (rate-limited, target-policy-checked).

## Re-verify

Clean entry (fresh browser, signed out): signed-out `/` → 0 console errors, remote avatars render (no fallbacks).

**Re-verification evidence (Pass 27, 2026-09-20, clean entry):** signed-out `/` → **CORS/`ERR_FAILED` console errors** on numeric-ID remote actor docs + **blank/fallback avatars**. STILL OPEN.

**Re-verification evidence (Pass 32, 2026-09-20, deployed `456b0d9`):** the S2/S14 anonymous-proxy seam is now live — the client routes signed-out remote actor reads through the same-origin `GET /ap/v1/proxy/{target}` instead of a direct cross-origin fetch (no more CORS/`ERR_FAILED` for username-path actors). However, the proxy **still 401s the unsigned GET** for Mastodon targets: `GET /ap/v1/proxy/https%3A%2F%2Fmastodon.social%2Fusers%2Fgnomon` → **401** `{"error":"Request not signed"}` (curl, no cookie, no Basic auth). Signed-out `/` → **11 console errors** (7× proxy 401 for Mastodon username + numeric-ID actors, 2× CORS, 2× `ERR_FAILED` for the numeric-ID direct fallback). The `isAnonymousRead` gate in `ProxyHandler` (`ActivityPubServerExtensions.cs:1885-1891`) should allow a cookie-less GET, but something upstream (likely a middleware or the route's auth policy) intercepts the unsigned GET and returns 401 before `ProxyHandler` runs. Hachyderm username actors also 401 (previously 200 via the direct `ACAO:*` path). STILL OPEN — the seam is deployed but not functional for unsigned GETs.
