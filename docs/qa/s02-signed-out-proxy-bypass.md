# S2 — Signed-out remote reads bypass the proxy (CORS/blank avatars)

- **Class:** bug (console-noise + broken avatars) — **Severity:** S2
- **Status:** open (re-confirmed Pass 27, 2026-09-20, on the rebuilt container)
- **Found:** Pass 11 (2026-09-20) — re-confirmed Passes 15, 20 (signed-out `/`), 23, 24, 25, 27
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
