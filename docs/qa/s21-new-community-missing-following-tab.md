# S21 — Newly created community missing from Following tab; /c/{handle} 404s

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open (found Pass 46, 2026-09-20, on deployed `65ccfa0`)
- **Found:** Pass 46 (2026-09-20)
- **Related:** [S19](s19-community-requests-tab-fails.md) (community page 404s), Phase 5 of unified home feed (community management page)

## Symptom

After creating a new community via `/communities` → "+ Create a community":

1. The community **does not appear** in the "Following" tab (only "technology" is listed).
2. The community **does appear** in the "All on this instance" tab (with correct name, handle, and description).
3. Navigating to `/c/{handle}` (e.g., `/c/qa-pass46-test`) → **"Sorry, there's nothing at this address."** (404).
4. The community **is stored in the database** (Group object with correct IRI exists in `Objects` table).
5. The community is **not followed** by the creator — no follow edge is created when a community is created.

Repro (andrew, logged in):
1. Navigate to `/communities`.
2. Click "+ Create a community".
3. Fill in Name: "QA Pass 46 test community", Handle: "qa-pass46-test".
4. Click "Create community" → POST `/ap/v1/u/andrew/outbox` → 202.
5. DB confirms: `SELECT "Id" FROM "Objects" WHERE "Document"->>'preferredUsername' = 'qa-pass46-test' AND "Document"->>'type' = 'Group'` → `https://iris.luit.ink/ap/v1/c/qa-pass46-test`.
6. "Following" tab: only "technology" (new community missing).
7. "All on this instance" tab: "qa-pass46-test" appears (with name "QA Pass 46 test community").
8. `/c/qa-pass46-test` → 404 "Not found".

## Root cause (suspected)

Two issues:

1. **No follow edge created on community creation:** When a user creates a community, the server should automatically follow the community on behalf of the creator (similar to how creating a post adds it to the outbox). The follow edge is missing, so the community doesn't appear in the "Following" tab. The community management page's "Following" tab is populated from `/ap/v1/u/{handle}/following`, which only returns actors/communities the user has explicitly followed.

2. **`/c/{handle}` route not wired:** The community page route `/c/{handle}` is not registered in the Blazor router. Communities are only reachable via `/actor?iri=…/ap/v1/c/{handle}` (the generic actor page). This is related to S19 (the community page 404s), but S19 was about the *existing* "technology" community; S21 confirms the route is missing for *all* communities.

## Fix

- **Follow on create:** In the server-side community creation handler, after creating the Group object, create a follow edge from the creator's actor to the new community (so the creator automatically follows their own community).
- **Wire `/c/{handle}` route:** Add a route `/c/{handle}` to the Blazor router that resolves the community by handle and renders the community detail page (with tabs for Posts, Members, Requests, etc.). This is the community-specific page distinct from the generic actor page.

## Re-verify

Clean entry, logged in:
1. Create a new community via `/communities` → "+ Create a community".
2. The new community appears in the "Following" tab immediately (no refresh needed).
3. Navigating to `/c/{handle}` renders the community detail page (not 404).
4. The community page has tabs for Posts, Members, and Requests (at minimum).
5. 0 console errors.

**Re-verification evidence (Pass 47, 2026-09-20, andrew, deployed `65ccfa0`):** After creating `qa-pass46-test` (Pass 46), manually followed the community via `/actor?iri=…/c/qa-pass46-test` → "Follow" button changed to "Unfollow" (follow succeeded). However, `/communities` → **Following tab still shows only "technology"** — `qa-pass46-test` still **missing** from the Following tab despite the follow edge now existing. "All on this instance" tab shows `qa-pass46-test`. `/c/qa-pass46-test` still 404. Actor page for `qa-pass46-test`: tabs **Posts (0), Followers (0), Following (0)** — no Requests, no Members. S21 confirmed: (1) no auto-follow on creation, (2) manual follow does not populate the Following tab (possible caching or filter issue), (3) /c/{handle} route missing. STILL OPEN.

**Re-verification evidence (Pass 48, 2026-09-20, andrew, deployed `65ccfa0`):** `/communities` → **Following tab now shows BOTH "technology" AND "qa-pass46-test"** (both with "Leave" button). The Pass 47 manual follow is now reflected — the Following tab was a **caching issue** (stale Blazor state), not a filter bug. "All on this instance" tab also shows both. `/c/qa-pass46-test` still 404 in Blazor SPA. Actor page for `qa-pass46-test`: tabs **Posts (0), Followers (1), Following (0)** — no Requests, no Members. S21 remaining scope: (1) **no auto-follow on community creation** (creator must manually follow their own community), (2) **/c/{handle} route still missing** (404s for all communities). PARTIALLY RESOLVED (caching issue resolved; auto-follow + /c/{handle} still open).

**Re-verification evidence (Pass 58, 2026-09-20, andrew, deployed `65ccfa0`):** Created new community `qa-pass58-test` via `/communities` → "+ Create a community" (Name: "QA Pass 58 test community", Handle: `qa-pass58-test`). After creation: (1) `/communities` → **Following tab shows only "technology"** — `qa-pass58-test` is **NOT in the Following tab** (no auto-follow on creation). "All on this instance" tab shows `qa-pass58-test`. (2) `/c/qa-pass58-test` → **404 "Not found"** in Blazor SPA (route still missing). (3) `/community?iri=…/c/qa-pass58-test` → **200** (community page renders with tabs: Feed, Members (0), Owners, Peers, Requests). (4) DB: Group document exists (`https://iris.luit.ink/ap/v1/c/qa-pass58-test`, ObjectType: Group, name: "QA Pass 58 test community"). 0 console errors on community page. S21 confirmed: (1) **no auto-follow on community creation** (creator must manually follow their own community), (2) **/c/{handle} route still missing** (404s for all communities — 6th consecutive pass). STILL OPEN.

**Re-verification evidence (Pass 65, 2026-09-20, andrew, deployed `65ccfa0` — NOTE: Dev fix `8539f3f` exists on `interop-testing` but is **NOT deployed** to the live container):** Created new community `qa-pass65-test` via `/communities` → "+ Create a community" (Name: "QA Pass 65 test community", Handle: `qa-pass65-test`). After creation: (1) `/communities` → **Following tab shows only "technology" + "qa-pass46-test"** — `qa-pass65-test` is **NOT in the Following tab** (no auto-follow on creation). (2) `/c/qa-pass65-test` → **502 Bad Gateway** (route still missing — 7th consecutive pass; 502 instead of 404, possibly a proxy-level response). (3) `/community?iri=…/c/qa-pass65-test` → **200** (community page renders: "QA Pass 65 test community · Iris"). S21 confirmed: (1) **no auto-follow on community creation** (creator must manually follow their own community), (2) **/c/{handle} route still missing** (502 for all communities — 7th consecutive pass). Dev fix `8539f3f` (auto-follow + CommunityHandleRedirect.razor) is on `interop-testing` but the live container was last started 11:05:19 and does not reflect the fix. STILL OPEN.
