# Inbox ③ Phase 1 — Fix the home-feed `Page` drop

**Date:** 2026-09-20
**Scope:** Client-only. Make Lemmy community posts (`Page` type) visible in the home timeline (`/home`) and public feed (`/`).

## Problem

The home-feed and public-feed `IsContentItem` filters only accepted `Note` and `Article` content objects. Lemmy community posts deserialize to the `Page` type (a `Document` subclass), so they were silently dropped from the home and public timelines. The community detail feed (`CommunityDetail.razor`) already accepted the base `Object` type and showed `Page` posts correctly — only the home/public feeds had the gap.

## Change

Added `Page` to both `IsContentItem` filters:

- `apps/Iris.Web.Client/Components/Pages/HomeTimeline.razor` — `/home` followed-feed filter (Create branch + bare-object branch).
- `apps/Iris.Web.Client/Components/Pages/Home.razor` — `/` public-feed filter (same two branches).

Both now read: `obj is Note || obj is Article || obj is Page`.

## Verification

- Web build green; 106 web tests pass; full solution build green.
- **Live (Playwright, Docker container rebuilt):** Signed in as `andrew`. Navigated to `/home`, loaded to page 6. The Lemmy `Page` post — *"138.11 fidelity check: Iris top-level post to Lemmy community via Iris's own delivery pipeline (Page type)"* by `alice` — **renders in the home feed** (as an `Announce` from the followed `c/interop` community). Before the fix, `Page` objects were excluded by the filter.
- The community detail feed (`/community?iri=…lemmy.luit.ink/c/interop`) already showed the same `Page` post (base-`Object` filter), confirming the deserialization path is correct.

## Notes

- `OutboxFilter.IsContentItem` (shared "Your posts" filter) still excludes `Page` — an actor's own `Page` posts are out of Phase 1 scope (community posts aren't an actor's own outbox items). Revisit if needed.
- `CountPostsAsync` (`ActivityPubServerExtensions.cs`) still counts `Note || Article` only — separate concern (profile post-count), not part of this slice.
- Remaining Inbox ③ phases: 2 (server `?source=` filter), 3 (`FeedBar`), 4 (wire tabs), 5 (management page), 6 (Profile tab), 7 (verify). See [docs/plans/unified-home-feed.md](../plans/unified-home-feed.md).
