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
