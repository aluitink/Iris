# 125.1 — Hardening: error & empty state audit

**Date:** 2026-09-13
**Type:** Review/audit (no code changes)
**Scope:** Playwright-driven audit of error, empty, and edge states across all pages

## Method

Signed in as `alice` via Playwright. Tested each edge case by navigating directly to the
relevant URL (404 IRIs, empty queries, actors with no posts, anonymous access). Verified
each renders a sensible message (not a crash, not a blank page, not a raw JSON dump).

## Results

| State | Page | Result | Verdict |
|---|---|---|---|
| 404 object | `/object?iri=…nonexistent…` | "Object not found." | ✅ |
| 404 actor | `/actor?iri=…nonexistent` | "Actor not found." | ✅ |
| 404 community | `/community?iri=…nonexistent` | "Community not found." | ✅ |
| Search no results | `/search?q=zzzz…` | "No matches found. Try a different handle or search term." | ✅ |
| Actor with no posts | `/actor?iri=…verifier87` | Stats show "2 posts" but Posts tab shows "No posts yet." | ❌ |
| Notification empty content | `/notifications` | One card shows avatar "B" with empty name and verb. | ❌ |
| Anonymous: home | `/home` | Redirects to login | ✅ |
| Anonymous: directory | `/directory` | Redirects to login | ✅ |
| Anonymous: communities | `/communities` | Redirects to login | ✅ |
| Anonymous: object detail | `/object?iri=…` | Redirects to login | ✅ |
| Anonymous: actor detail | `/actor?iri=…bob` | Renders profile (public) | ✅ |

## Findings

### 126.1 — Actor detail stats row counts non-post activities (MEDIUM)

The stats row (added in 123.2) reads `totalItems` from the actor's outbox, which counts
**all** outbox activities (Create, Follow, Like, Announce, etc.), not just posts
(Note/Article). An actor who created a community and followed someone but never posted
shows "2 posts" in the stats row while the Posts tab correctly shows "No posts yet."

**Fix:** Count only Note/Article objects in the outbox, not all activities. The server
could expose a `postCount` field on the actor document, or the client could filter the
first page of the outbox for Note/Article types.

### 126.2 — Some notification rows render with empty name and verb (LOW)

One notification card in alice's inbox renders the avatar (initial "B") but the actor
name link and the verb text are both empty. The card's body (the "View →" link) renders
correctly. This suggests the `GetDisplayName` returned null and `DisplayNameFallback`
also returned null, or the `VerbFor` returned an empty string.

**Fix:** Investigate which activity type produces this. Ensure `DisplayNameFallback`
always returns a non-empty string (it currently falls back to the IRI host, which should
be non-empty). Add a fallback verb for unknown activity types.

## Console errors

- 404 pages: 1 expected 404 console error each (the API returning 404). No unexpected errors.
- All other pages: 0 console errors.

## No other issues found

All other edge states render correctly. The app handles 404s, empty searches, and
anonymous access gracefully.
