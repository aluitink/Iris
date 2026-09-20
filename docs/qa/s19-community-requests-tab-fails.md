# S19 — Community "Requests" tab always fails to load (no request fires, no retry)

- **Class:** bug — **Severity:** S2
- **Status:** open (re-confirmed Pass 39, 2026-09-20, on deployed `4f5dd5c`) — new facet: follow requests in notifications have no Accept/Decline UI
- **Found:** Pass 30 (2026-09-20) — re-confirmed Passes 34, 36
- **Related:** [s04](s04-communities-following-remote.md), [s06](s06-remote-join-csp-blocked.md) (community join flow), [s09](s09-report-silent-noop.md) (silent-failure pattern)

## Symptom

On a **community detail** page (e.g. `technology`), the **Requests** tab — which is where the owner/moderator approves or rejects join requests — always shows:

> "We couldn't load the join requests. Please try again."

and there is **no Refresh button** and no way to retry. The other four tabs (Feed, Members, Owners, Peers) all load correctly.

Repro (andrew, owner of `technology`):
1. Communities → open the `technology` community.
2. Click the **Requests** tab → the error banner renders.
3. Re-click the tab → the same error re-renders.

## What the network/console show (the failure is silent)

- **No network request fires** for the Requests tab — the only community-scoped call observed is `GET /local/v1/c/technology/owners` (200) for the Owners tab. There is no `…/requests` (or equivalent) call at all.
- **No console error** is logged for it (the only console errors on the page are environmental 502s from the `lemmy.ml` proxy for feed items).
- So the tab's data source is missing or broken client-side: it neither calls a working endpoint nor surfaces the underlying error — it just renders a generic failure with no retry.

## Root cause

The Requests tab is wired to a data source that doesn't resolve to a working endpoint (or the endpoint it calls isn't the one the server exposes), and the failure is swallowed into a static "couldn't load" message with no retry control. Compare: Members (`/ap/v1/c/<name>/members`), Owners (`/local/v1/c/<name>/owners`), and Peers/following all have working endpoints; Requests has none that fires.

Expected: the Requests tab loads the community's pending join requests (empty list if none) and gives the owner **Approve / Reject** controls — this is the moderation entry point for community joins.

## Fix

- Point the Requests tab at the correct endpoint for a community's pending join requests (add it if the server doesn't expose one yet).
- On success, render the pending requests with **Approve / Reject** actions; on an empty set, show a sensible "No pending requests" state.
- On a genuine failure, log the error and provide a **Refresh/retry** control instead of a dead-end message.

## Re-verify

Clean entry, owner of a community with (and without) pending join requests:
- Requests tab shows the pending requests (or "No pending requests") — not the failure banner.
- Approving/rejecting a request updates the member list and the request disappears.
- If the endpoint is down, the tab shows a retryable error (Refresh button), not a dead-end.

**Re-verification evidence (Pass 34, 2026-09-20, andrew, deployed `456b0d9`):** Community `technology` → Requests tab → **"We couldn't load the join requests. Please try again."** — no Refresh button, no retry. No network request fires for the tab. 0 non-environmental console errors. STILL OPEN.

**Re-verification evidence (Pass 36, 2026-09-20, andrew, deployed `bb28dcf`):** Community `technology` (andrew, owner) → Requests tab → **"We couldn't load the join requests. Please try again."** — no Refresh button, no retry. Only network request: `GET /ap/v1/c/technology/members` (200, for Members tab). No requests-specific call fires. 0 console errors. STILL OPEN.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** Community `technology` (andrew, owner) → Requests tab → **"We couldn't load the join requests. Please try again."** — no Refresh button, no retry. No requests-specific network call fires (filtered on `requests|join` — zero matches). 0 console errors. STILL OPEN.

**Re-verification evidence (Pass 39, 2026-09-20, andrew, deployed `4f5dd5c`):** Community `technology` (andrew, owner) → Requests tab → **"We couldn't load the join requests. Please try again."** — no Refresh button, no retry. `/requests` route → **404 "Not found"**. andrew's notifications page shows "qa39test sent you a follow request" (and older requests from qa34test, qa36test) but **NO Accept/Decline buttons** — the follow requests are visible but cannot be acted on. This is a new facet: the moderation entry point for follow/join requests is broken in two places (community Requests tab dead-end + notification items lack action buttons). STILL OPEN.

**Re-verification evidence (Pass 41, 2026-09-20, andrew, deployed `59ff4ec`):** Community `technology` (andrew, owner) → Requests tab → **"We couldn't load the join requests. Please try again."** — no Refresh button, no retry. Only network request: `GET /ap/v1/c/technology/members` (200, for Members tab). No requests-specific call fires. 0 console errors. STILL OPEN.

**Re-verification evidence (Pass 42, 2026-09-20, andrew, deployed `65ccfa0`):** Community `technology` (andrew, owner) → Requests tab → **"We couldn't load the join requests. Please try again."** — no Refresh button, no retry. No requests-specific network call fires. 0 console errors. STILL OPEN.
