# S49 — Community creation is a silent no-op (no error, community not created)

- **Class:** bug — **Severity:** S2
- **Status:** open (partially resolved — see Pass 319)
- **Found:** Pass 318 (2026-09-22)
- **Updated:** Pass 319 (2026-09-22)
- **Related:** S21 (newly created community missing from Following tab — fixed, but creation itself now fails entirely)

## Symptom

On `https://qa-iris-b.luit.ink/communities`, clicking "+ Create a community" opens a form with Name, Handle, and Description fields. After filling in:
- Name: `QA Pass 318 Community`
- Handle: `qa-pass-318`
- Description: `QA pass 318 community test`

and clicking the "Create community" button, the form closes silently. **No error message is shown.**

**Pass 318 observation (stale WASM):** The community did NOT appear in any tab. 0 console errors.

**Pass 319 observation (fresh browser context):** After a full page reload, the community DOES appear in all three tabs ("My communities", "All on this instance", "Following") with "You are the only owner" and a "Manage peers" link. The DB object exists (`https://qa-iris-b.luit.ink/ap/v1/c/qa-pass-318`, Group) and `GET /ap/v1/c/qa-pass-318` returns 200. **The original "silent no-op" was likely a stale WASM cache issue, not a server-side bug.**

## New facet (Pass 319): Community feed is empty

After posting a note to the community via "Post to this community" (HTTP 202, note ID `06GCNKS5FN696JZA1E0MDA219C`):

1. The note IS visible in ii-b1's home feed and profile (85 posts).
2. The note's AP object document includes `attributedTo: [ii-b1, qa-pass-318]` and `to: [qa-pass-318/followers, #Public]`.
3. `GET /ap/v1/c/qa-pass-318/feed` returns 200 with `orderedItems: []` — the note is NOT in the community feed.
4. The community feed UI shows "No posts in this community yet." even after clicking Refresh.
5. `GET /ap/v1/c/qa-pass-318/followers` returns `totalItems: 0` — ii-b1 (the owner) is not listed as a follower of their own community.
6. No Edges row links the note to the community (no `Kind` 10/11 edge between the note and the community).

**Root cause hypothesis:** The community feed query filters by community followers, but the owner is not automatically added to the followers collection upon community creation. The note's `to` includes the community's followers collection, but since the collection is empty, no one (including the owner) can see the post in the community feed.

**Contrast:** The `ii-a8-community` on instance A has `totalItems: 1` (ii-b1) in its followers collection, and its feed shows posts correctly.

## Fix

TBD — needs investigation of:
1. Why the owner is not automatically added to the community's followers collection upon creation.
2. Why the community feed query does not include posts attributed to the community when the followers collection is empty.

## Re-verify

1. On `https://qa-iris-b.luit.ink/communities`, create a new community.
2. Verify it appears in all three tabs (fresh browser context).
3. Post a note to the community.
4. Verify the note appears in the community feed.
5. Verify `GET /ap/v1/c/<handle>/feed` includes the note in `orderedItems`.
6. Verify the owner is listed in `GET /ap/v1/c/<handle>/followers`.
7. 0 console errors.
