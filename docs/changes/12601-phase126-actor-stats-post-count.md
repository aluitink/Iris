# 126.1 — Actor detail stats row counts only Note/Article posts

**Date:** 2026-09-13
**Type:** Bug fix
**Scope:** `ActorDetail.razor` — `PostCount` calculation

## Problem

The stats row (added in 123.2) read `totalItems` from the actor's outbox collection.
The outbox contains **all** activities (Create, Follow, Like, Announce, Undo, Flag, etc.),
not just posts. An actor who created a community and followed someone but never posted
showed "2 posts" in the stats row while the Posts tab correctly showed "No posts yet."

## Fix

Replaced `GetFirstPageTotalAsync(outboxIri)` with a new `GetPostCountAsync` that:
1. Fetches the first page of the outbox (limit 30, via `GetCollectionAsync`).
2. Iterates the page items and counts only those whose `Object` (first item in the
   `Object` collection) is an `IObject` with `Type` containing "Note" or "Article."

A parallel `GetPostCountAnonymousAsync` handles the signed-out path (plain HTTP GET
with `limit=30`, parsing `orderedItems`/`items` from the JSON).

## Tradeoff

For actors with more than 30 outbox items, the count is a lower bound (only the first
page is fetched). For most users this is accurate; for prolific posters the count may
be understated. A fully accurate count would require a server-side `postCount` field
on the actor document (DB query), which is a larger change.

## Verification

- **verifier87** (0 posts, 2 non-post activities: Create Group + Follow):
  - Before: "2 posts" / After: "0 posts" ✅
  - Posts tab: "No posts yet." ✅
- **bob** (4 Note objects, 9 non-post activities in outbox):
  - Before: "13 posts" (totalItems) / After: "4 posts" ✅
  - Posts tab shows 4 posts ✅
- 0 console errors on both pages.
- Full test suite green: 1,979 pass, 0 fail, 1 skip.

## Files changed

- `apps/Iris.Web.Client/Components/Pages/ActorDetail.razor` — replaced `PostCount`
  calculation in `LoadCountsAsync`; added `GetPostCountAsync` and
  `GetPostCountAnonymousAsync`.
