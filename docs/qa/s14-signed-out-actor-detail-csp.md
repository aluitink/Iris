# S14 — Signed-out remote actor-detail is CSP-blocked (S2's actor-detail facet)

- **Class:** bug — **Severity:** S2
- **Status:** open (re-confirmed Pass 27, 2026-09-20, on the rebuilt container)
- **Found:** Pass 20 (2026-09-20) — re-confirmed Pass 27
- **Related:** [S2](s02-signed-out-proxy-bypass.md) (same root cause, root-page facet)

## Symptom

Signed-out **remote actor detail** (`/actor?iri=https://lemmy.luit.ink/u/lemmyadmin`) does a **direct browser fetch** to the remote actor IRI → **blocked by CSP `connect-src 'self'`** (4 console errors: 2× CSP-violation + 2× "Refused to connect") → the page shows **"Failed to load actor. It may not exist or the server is unreachable."** A remote actor's profile **cannot be viewed at all when signed out**, even though the actor is resolvable (it renders fine signed in, which routes through the proxy).

## Root cause

Same root as S2 — `UiContext.FetchActorAsync:489` skips the proxy when signed out — but this is a **separate code path**: the actor-detail page has its own signed-out read that fetches the remote actor IRI directly, so it breaks for **any** remote actor (Lemmy/Mastodon), not just numeric-ID ones.

## Fix

Route the signed-out remote actor-detail read through the same **anonymous proxy GET path** as the signed-out root page (the S2 fix).

## Re-verify

Signed-out `/actor?iri=<remote actor IRI>`: the profile renders (banner, avatar, handle) with 0 console errors.

**Re-verification evidence (Pass 27, 2026-09-20, clean entry):** signed-out `/actor?iri=https://lemmy.luit.ink/u/lemmyadmin` → **CSP-blocked direct fetch** → "Failed to load actor." STILL OPEN.
