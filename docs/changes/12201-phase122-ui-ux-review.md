# 122.1 — General UI/UX review (second pass)

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Scope

Visual inspection of all pages (desktop 1400px + mobile 375px) via Playwright, signed in as `alice`. Covered: `/home`, `/notifications`, `/directory`, `/communities`, `/community?iri=...`, `/object?iri=...`, `/actor?iri=...`, `/profile`, `/settings`, `/compose`, `/search`, `/login`, `/register`. This pass focuses on changes made in 121.* and any remaining rough edges.

## Findings

### 123.1 — Notifications: deduplicate repeated follow requests (HIGH)

**Page:** `/notifications`
**Severity:** S2 (user-visible data integrity issue)
**Class:** bug

When an actor sends multiple follow requests to the same recipient (e.g., Bob clicks "Follow Alice" 4 times during testing), Alice's notification inbox shows 4 identical "Bob sent you a follow request" cards instead of one.

**Root cause:** The server mints a fresh ULID IRI for every `Follow` activity (`MintActivityIds`, `ActivityPubServerExtensions.cs:2859`). All existing dedup is keyed on the activity IRI (`AddToInboxAsync` PK is `(Direction, ActorId, ItemIri)`), so each new Follow IRI passes as "new" and gets a separate `BoxItems` row. The `Edges` table correctly collapses the follow relationship to one `(Follow, bob, alice)` edge, but the inbox write at `ActivityPubServerExtensions.cs:3055-3064` is unconditional — it does not check whether the follow edge is new before calling `AddToInboxAsync`.

**Fix options (see research):**
- **Write-time gate (Option B):** In the local outbox-publish follow branch, gate the `AddToInboxAsync` call on whether the follow edge is new (consult `persistence.Follows.IsFollowingAsync`). If the edge already exists, skip the inbox write. Mirror in the inbound federation path (`InboxProcessor.ProcessAsync`).
- **Read-time collapse (Option A):** In `FilterInboxByPrefs` (`WebAppFactory.cs:1258-1338`), collapse items sharing `(actor IRI, type, target IRI)` into one for event types like `Follow`. This also fixes the unread-count badge (which calls the same filter).

Recommended: both — B stops creating duplicates going forward, A acts as a safety net for other event types and pre-existing rows.

### 123.2 — Actor detail: enrich the profile header (MEDIUM)

**Page:** `/actor?iri=...`
**Severity:** S3 (UX polish)
**Class:** UX

The actor profile page header shows only the handle as a page title and a minimal card with an avatar circle and the name. There is no bio, no follower/following counts, no follow/unfollow button, and no "joined" date. Compare to the community detail page (`/community?iri=...`) which shows name, handle, description, and action buttons (Edit community, Post to this community) — the actor page is noticeably sparser.

**Improvement:** Show the actor's `summary` (bio) in the header card, follower/following/post counts, and a Follow/Unfollow button (or "Following" state) for other actors. For the actor's own profile, show an "Edit profile" button linking to settings.

### 123.3 — Community detail: mobile layout for ownership banner (LOW)

**Page:** `/community?iri=...`
**Severity:** S3 (UX polish)
**Class:** UX

On mobile (375px), the "This is your community." text + "Edit community" button + "0 members" text all compete for space in a single row and wrap awkwardly. The button gets squeezed between the two text fragments.

**Improvement:** On mobile, stack the ownership banner: text on one line, button below (or full-width). Similar to how 121.3 handled the notifications header on mobile.

## Pages verified clean (no issues found)

- `/home` (desktop + mobile) — timeline renders, action buttons, mobile nav menu
- `/directory` (desktop) — actor cards, search
- `/communities` (desktop + mobile) — community list + create form
- `/object?iri=...` (desktop + mobile) — content, replies, Edit/Delete for own posts
- `/profile` (desktop) — 5 tabs, posts list
- `/settings` (desktop) — 3 tabs with expandable subsections
- `/compose` (desktop + mobile) — textarea, attachment, type/audience selectors, CW, formatting tips
- `/search` (desktop) — search box + actors-only toggle
- `/login` (mobile) — sign-in form
- `/register` (mobile) — create-account form

0 console errors on every page.

## No code changes in this slice

This is a review-only slice. Findings are logged as new `123.*` items in PLAN.md's Up Next.
