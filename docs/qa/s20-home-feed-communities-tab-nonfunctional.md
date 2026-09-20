# S20 — Home feed "Communities" tab is non-functional (no API call, same content as Posts)

- **Class:** bug / feature-gap — **Severity:** S2
- **Status:** open (found Pass 42, 2026-09-20, on deployed `65ccfa0`)
- **Found:** Pass 42 (2026-09-20)
- **Related:** Phase 4 of unified home feed (docs/plans/unified-home-feed.md) — "wire the tabs to the feed"

## Symptom

On `/home`, the bottom control strip has two tab buttons: **Posts** and **Communities**. Clicking **Communities**:

1. Fires **0 new API requests** (no `?source=` parameter change, no new feed fetch).
2. Shows **identical content** to the Posts tab (same first post, same items — verified by comparing the first listitem's text content before and after the tab switch).
3. The tab visually becomes "active" (CSS class `tab--active` applied), but the underlying feed data does not change.

Repro (andrew, logged in):
1. Navigate to `/home`.
2. Note the first post in the feed (e.g., "skinnylatte" dolphin boost).
3. Click the **Communities** tab button.
4. Wait 5 seconds.
5. The first post is still "skinnylatte" dolphin boost — identical to Posts tab.
6. Network tab shows only the initial 3 feed requests (`/ap/v1/u/andrew/feed`, `?page=2`, `?page=3`) — no new requests fired by the tab switch.

## Root cause (suspected)

Phase 4 of the unified home feed plan was supposed to wire `HomeTimeline.razor` to read `iris:homeTab` and set `FeedIri` = `{actor}/feed?source=…` based on the active tab. The tab switch updates the CSS active state (FeedBar ↔ HomeTimeline sync works) but does **not** trigger a new feed fetch with a different `source` parameter. Either:

- The `FeedIri` is not being updated when the tab changes (state change not triggering re-render/refetch), or
- The `source` parameter is not being sent to the server, or
- The server ignores the `source` parameter and returns the same feed regardless.

## Fix

- Verify `HomeTimeline.razor` reads `iris:homeTab` and constructs `FeedIri` with the correct `?source=` value for each tab.
- Ensure a tab switch triggers a new feed fetch (not just a CSS class change).
- Verify the server's `/ap/v1/u/{handle}/feed` endpoint accepts and honors the `source` query parameter (e.g., `source=posts` vs `source=communities`).
- If the server does not yet support `source` filtering, implement it or document the gap.

## Re-verify

Clean entry, logged in:
1. `/home` → Posts tab shows the user's following feed.
2. Click **Communities** tab → a new API request fires with `?source=communities` (or equivalent), and the feed content changes to show community-specific posts (different from Posts tab).
3. Click back to **Posts** tab → another new API request fires with `?source=posts` (or equivalent), and the feed reverts to the following feed.
4. 0 console errors.

**Re-verification evidence (Pass 43, 2026-09-20, andrew, deployed `65ccfa0`):** `/home` → Posts tab shows following feed (skinnylatte dolphin boost first). Click **Communities** tab → 0 new API requests fire (only initial 3 feed requests: `/ap/v1/u/andrew/feed`, `?page=2`, `?page=3`). First post is still skinnylatte dolphin boost — **identical to Posts tab**. Tab visually becomes active but feed data does not change. STILL OPEN.

**Re-verification evidence (Pass 49, 2026-09-20, andrew, deployed `65ccfa0`):** `/home` → Posts tab shows following feed (skinnylatte dolphin boost first). Click **Communities** tab → **same `GET /ap/v1/u/andrew/feed` requests** as Posts tab (no `?source=` parameter, no community-specific call). First post is still skinnylatte dolphin boost — **identical to Posts tab**. API probe: `GET /ap/v1/u/andrew/feed?source=communities` → **403** (server rejects the `source` parameter). `GET /ap/v1/u/andrew/feed` (no param) → 200 (works). The server does not support `source` filtering — the Communities tab has no backing endpoint. STILL OPEN (server-side gap confirmed: `?source=` param returns 403).

**Re-verification evidence (Pass 55, 2026-09-20, andrew, deployed `65ccfa0`):** `/home` → Posts tab shows following feed (skinnylatte dolphin boost first). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content is **identical to Posts tab** (same first post, same items). 0 console errors. Click back to **Posts** tab → 0 new API requests (feed unchanged). The Communities tab is a **visual-only toggle** — it changes the CSS active state but does not trigger a new feed fetch, does not send a `?source=` parameter, and the feed data never changes. The tab is non-functional. STILL OPEN.

**Re-verification evidence (Pass 61, 2026-09-20, andrew, deployed `65ccfa0`):** `/home` → Posts tab shows following feed (WeirdWriter boost first, 8 feed requests: `/ap/v1/u/andrew/feed` ×2 + `?page=2` ×2 + 2 more). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content is **identical to Posts tab** (same first post: WeirdWriter boost, same items). 0 console errors. The Communities tab is a **visual-only toggle** — it changes the CSS active state but does not trigger a new feed fetch, does not send a `?source=` parameter, and the feed data never changes. STILL OPEN (4th consecutive pass confirming visual-only toggle).

**Re-verification evidence (Pass 67, 2026-09-20, andrew, Dev fix deployed):** `/home` → Posts tab loads feed (`GET /ap/v1/u/andrew/feed` → 200, `?page=2` → 200). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content is **identical to Posts tab**. The Communities tab is a **visual-only toggle** — it changes the CSS active state but does not trigger a new feed fetch. STILL OPEN (5th consecutive pass confirming visual-only toggle).

**Re-verification evidence (Pass 73, 2026-09-20, andrew, Dev fix deployed):** `/home` → Posts tab loads feed (`GET /ap/v1/u/andrew/feed` → 200, `?page=2` → 200). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content identical to Posts tab. 0 console errors. STILL OPEN (6th consecutive pass confirming visual-only toggle).

**Re-verification evidence (Pass 77, 2026-09-20, andrew, Dev fix deployed):** `/home` → Posts tab loads feed (`GET /ap/v1/u/andrew/feed` → 200, `?page=2` → 200). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content identical to Posts tab. 0 console errors. STILL OPEN (7th consecutive pass confirming visual-only toggle).
