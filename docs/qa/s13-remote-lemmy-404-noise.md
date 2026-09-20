# S13 — Remote Lemmy object-detail logs expected proxy 404s (console noise)

- **Class:** UX / bug (console-noise) — **Severity:** S3
- **Status:** open (re-confirmed Pass 37, 2026-09-20, on deployed `bb28dcf`)
- **Found:** Pass 19 (2026-09-20); data point added Pass 21 — re-confirmed Passes 27, 37
- **Related:** distinct from [S3](s03-object-detail-create-iri-404.md) (local Create-IRI `/replies` 404)

## Symptom

Opening a **remote Lemmy post** (`/object?iri=https://lemmy.luit.ink/post/1`) logs **3 console 404 errors**: `POST /ap/v1/proxy/https://lemmy.luit.ink/post/1/{replies|likes|shares}` all 404. The UI degrades gracefully ("No replies yet", Likes/Shares tabs render) and the post itself renders (the post doc loads via proxy → 200) — but the expected 404s are not suppressed → 3 errors per remote-Lemmy post detail.

## Root cause

The object-detail page (Replies/Likes/Shares tabs) requests **ActivityStreams collection paths** from the remote object, but **Lemmy does not expose them** — Lemmy serves post data via `/api/v3/post?id=1`, not AS collection endpoints. Confirmed by hitting Lemmy directly: `/post/1` → 200, `/post/1/replies|likes|shares` → 404. The proxy faithfully relays the 404s.

**Data point (Pass 21):** on `lemmy.luit.ink/post/2` the header shows **"2 comments"** (from Lemmy's `/api/v3/post` payload) but the **Replies tab says "No replies yet"** — the comments *count* (Lemmy-side) and the *replies list* (Iris-side, empty because of the 404s) are inconsistent.

## Fix

For remote Lemmy posts, don't request `/replies` / `/likes` / `/shares` (derive counts and the reply list from the `/api/v3/post` payload), or treat 404s on these optional collection fetches as "empty" (and keep the displayed comment count consistent with the list).

## Re-verify

Open a remote Lemmy post's object detail: 0 console 404s; the comment count and the Replies tab agree (both derived from the same Lemmy source, or the count is hidden when replies can't be loaded).

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** `lemmy.luit.ink/post/1` detail → **3 console 404s** (`POST …/proxy/…/post/1/{replies|likes|shares}`), post renders, UI degrades gracefully. STILL OPEN.

**Re-verification evidence (Pass 37, 2026-09-20, andrew, deployed `bb28dcf`):** `lemmy.luit.ink/post/1` detail → **3 console 404s** (`GET …/proxy/…/post/1/{replies|likes|shares}`), post renders ("Hello from Lemmy interop"), Replies tab shows "No replies yet". STILL OPEN.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** `lemmy.luit.ink/post/1` detail → **3 console 404s** (`GET …/proxy/…/post/1/{replies|likes|shares}`), post renders, UI degrades gracefully. STILL OPEN.
