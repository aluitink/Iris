# 123.2 — Actor detail: enrich the profile header

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Problem

The actor profile page (`/actor?iri=...`) header was sparser than the community detail page. It showed only the avatar, handle, and action buttons — no post/follower/following counts in the header. The community page shows "N members" as a stat; the actor page had no equivalent.

## Fix

Added a **stats row** (post count, follower count, following count) below the `ActorProfile` card in the actor detail header.

### Changes

1. **`ActorDetail.razor`**: Added `PostCount` field (loaded from outbox `totalItems` via the same `GetFirstPageTotalAsync`/`GetFirstPageTotalAnonymousAsync` helpers already used for follower/following counts). Added `HasStats` computed property to gate the stats row (avoids the Blazor CS0136/CS0165 pattern-variable-across-`||` compile error). Wrapped the `ActorProfile` + stats in a new `.actor-detail-header-main` flex column.

2. **`app.css`** (both client + server copies): `.actor-detail-header-main` (flex column, gap), `.actor-detail-stats` (wrapping row, secondary text color), `.actor-detail-stat strong` (primary text color, 600 weight).

### What was NOT added

- **Bio/summary:** Already rendered by `ActorProfile` when present. Bob's actor doc has no `summary`, so there's nothing to show. The component handles it correctly.
- **Follow/Unfollow button:** Already present via `FollowButton` (for non-self, signed-in users).
- **Moderation buttons:** Already present via `ModerationActions`.
- **"Joined" date:** No `created`/`joined` timestamp is stored in the actor entity or served in the actor JSON. Adding one would require a DB migration + server change — out of scope for a UI polish slice.
- **"Edit profile" button:** The Settings nav item already provides access to profile editing.

## Verification

- `dotnet build` clean (0 warnings, 0 errors).
- `dotnet test` green: 1,823 pass, 0 fail.
- Live-verified (Playwright, Docker app):
  - Bob's page: "13 posts, 2 followers, 1 following" + Unfollow/Block/Mute/Report buttons.
  - Alice's own profile: "27 posts, 2 followers, 1 following" + no follow/moderation buttons (correct).
  - Mobile (375px): stats row wraps, header stacks vertically.
  - 0 console errors.
