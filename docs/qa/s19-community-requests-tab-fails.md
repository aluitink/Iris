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

**Re-verification evidence (Pass 43, 2026-09-20, andrew, deployed `65ccfa0`):** Community `technology` — `/c/technology` route → **"Not found"** (404 in browser, 200 via curl but Blazor SPA renders "Sorry, there's nothing at this address"). The community can only be reached via `/actor?iri=…/ap/v1/c/technology`, which renders the generic actor page with tabs: **Posts (0), Followers (2), Following (0)** — **no Requests tab, no Members tab**. The S19-specific Requests tab is now **unreachable** (the community page that had it 404s). Notifications still show follow requests from qa34test/qa36test/qa39test with **NO Accept/Decline buttons**. S19 scope has changed: the Requests tab is gone (community page 404s), and the notification action buttons are still missing. STILL OPEN (scope changed).

**Re-verification evidence (Pass 47, 2026-09-20, andrew, deployed `65ccfa0`):** Notifications page: follow requests from qa34test/qa36test/qa39test still visible with **NO Accept/Decline buttons** — only "View andrew's profile" link. Community `qa-pass46-test` actor page: tabs **Posts (0), Followers (0), Following (0)** — **no Requests, no Members tab** (consistent with `technology`). `/c/technology` and `/c/qa-pass46-test` both 404. S19 confirmed: the community-specific Requests/Members tabs are absent from all community actor pages, and notification follow-request items lack action buttons. STILL OPEN.

**Re-verification evidence (Pass 48, 2026-09-20, andrew, deployed `65ccfa0`):** Community `technology` actor page: tabs **Posts (0), Followers (2), Following (0)** — **no Requests, no Members tab** (unchanged). Followers tab: andrew + 1 other. API: `GET /ap/v1/c/technology/members` → **200**, `GET /ap/v1/c/technology/requests` → **404**, `GET /local/v1/c/technology/owners` → **403** (unauthenticated). Community `qa-pass46-test` actor page: same tab structure (Posts/Followers/Following only), Followers (1). `/c/technology` and `/c/qa-pass46-test` both 404 in Blazor SPA. Notifications: follow requests from qa34test/qa36test/qa39test still visible with **NO Accept/Decline buttons**. The `/ap/v1/c/{name}/requests` endpoint does not exist (404) — the Requests tab has no backing endpoint. STILL OPEN.

**Re-verification evidence (Pass 51, 2026-09-20, andrew, deployed `65ccfa0`):** Notifications → **Follows tab**: 5 follow requests visible — qa39test (1h ago), qa36test (2h ago), qa34test (3h ago), New User/newuser1 (1d ago), RayvenMX/mastodon.world (1d ago). **ALL have NO Accept/Decline buttons** — each shows only "View andrew's profile" link. The Follows filter tab correctly surfaces follow requests but provides no action buttons to accept or decline them. 0 console errors. STILL OPEN (now 5 follow requests pending, none actionable).

**Re-verification evidence (Pass 52, 2026-09-20, andrew, deployed `65ccfa0`):** New community `qa-pass51-test` created via `/communities` → "+ Create a community". **Owner view** (`/community?iri=…/c/qa-pass51-test`): tabs **Feed, Members (0), Owners, Peers, Requests**. **Requests tab** shows alert: *"We couldn't load the join requests. Please try again."* — **NO network request fired** (UI error without API call). API: `GET /ap/v1/c/qa-pass51-test/requests` → **404** (endpoint does not exist). `GET /ap/v1/c/qa-pass51-test/members` → **200** (empty). `GET /local/v1/c/qa-pass51-test/owners` → **200** (andrew, Owner). **NEW FINDING — Edit community Save is a silent no-op:** "Edit community" form has fields (Name, Description, Icon, "Require approval for join requests" checkbox). Clicking Save fires `POST /ap/v1/c/qa-pass51-test/outbox` → **202 Accepted**, but the request body contains **only the original Group document** (no `requireApproval`, no changes) — the checkbox state is **not included in the Update activity**. DB `Objects` document is **unchanged** after Save. The "Require approval for join requests" setting is **not persisted** — the entire Edit community feature is a silent no-op. STILL OPEN.

**Re-verification evidence (Pass 57, 2026-09-20, andrew, deployed `65ccfa0`):** Community `qa-pass46-test` (andrew, owner) → "Edit community" → checked "Require approval for join requests" checkbox → clicked Save. **DB verification:** `SELECT "Id", "ObjectType", "CreatedAt" FROM "Objects" WHERE "Document" @> '{"type":"Update"}' AND "Document"->>'actor' = 'https://iris.luit.ink/ap/v1/u/andrew'` → **0 rows** (no Update activity persisted). Reopened Edit community form → checkbox `edit-community-approve-members` is **unchecked** (state did not persist). The Edit community Save is a **silent no-op** — the checkbox state is not included in the Update activity, and the DB document is unchanged. 3 console errors (502 on lemmy.ml proxy — unrelated to S19). **Notifications → All tab:** 3 follow requests visible (qa39test 2h ago, qa36test 3h ago, qa34test 4h ago) — **ALL have NO Accept/Decline buttons** (only "View andrew's profile" link). STILL OPEN (Edit community no-op re-confirmed on different community; notification action buttons still missing).

**Re-verification evidence (Pass 63, 2026-09-20, andrew, deployed `65ccfa0`):** Community `technology` (andrew, owner) → **Requests tab** → alert: *"We couldn't load the join requests. Please try again."* — **NO network request fired** for the requests endpoint (0 requests matching `requests|join` in network log). API probe: `GET /ap/v1/c/technology/requests` → **404** (endpoint does not exist). `GET /ap/v1/c/technology/members` → **200**. The Requests tab has no backing endpoint and no retry mechanism (no Refresh button). STILL OPEN (requests endpoint still 404s; UI error without API call; no retry).

**Re-verification evidence (Pass 70, 2026-09-20, andrew, Dev fix deployed):** (1) **Edit community Save is STILL a silent no-op** — `qa-pass46-test` → "Edit community" → "Require approval for join requests" checkbox is **checked** (pre-set state from Pass 57) → clicked Save. Reopened form → checkbox still **checked** (UI state preserved). DB: `SELECT "Document"->>'requireApproval' FROM "Objects" WHERE "Document"->>'id' = '…/c/qa-pass46-test'` → **NULL** (no `requireApproval` field in the Group document). `SELECT * FROM "Objects" WHERE "Document" @> '{"type":"Update"}' AND "Document"->>'actor' = '…/u/andrew'` → **0 rows** (no Update activity persisted). The checkbox state is **not persisted to the DB**. (2) **Notifications → All tab:** 3 follow requests (qa39test 3h ago, qa36test 4h ago, qa34test 4h ago) — **ALL have NO Accept/Decline buttons** (only "View andrew's profile" link). STILL OPEN (Edit community no-op re-confirmed; notification action buttons still missing).
