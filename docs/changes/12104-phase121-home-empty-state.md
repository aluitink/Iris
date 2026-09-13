# 121.4 — Home timeline: add empty state when user has no follows

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Scope

120.1 flagged that a user with no follows sees a blank home timeline page.

## Finding

The empty state was already implemented in Phase 34.25 (`HomeTimeline.razor` lines 19–28): when the `PagedCollection` loads zero items it renders an SVG icon + "Your timeline is empty." + "Follow people to see their posts here." + a "Browse the directory →" link to `/directory`. This was added well before 120.1's review.

## Action

No code change needed. The 120.1 finding was based on a stale observation. Marking 121.4 as already satisfied.
