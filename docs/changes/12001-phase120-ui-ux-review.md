# 120.1 — General UI/UX review (first pass)

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Scope

Visual inspection of every page on the live Docker app (desktop 1400px + mobile 375px), signed in as alice. Pages reviewed: public timeline, home timeline, profile, compose, notifications, directory, communities, search, settings, actor profile, object detail, login, register.

## Findings

### 1. Notification links show raw IRIs (121.1)

**Where:** Notifications page, all notification types (follow requests, likes, boosts).
**Issue:** The clickable link under each notification shows a raw IRI fragment like `u alice` or `notes 06G84EEMA8PZR6FPZSJSVH2PPR`. This is the `Id` field of the ActivityPub object, not a human-readable label. On desktop it's small and easy to miss; on mobile it's more prominent and looks broken.
**Expected:** Show a friendly label — e.g. for a follow request, "View alice's profile"; for a like, show the note's content preview (truncated) or "View note".
**Severity:** Medium — confusing to users, looks like a bug.

### 2. Compose mobile: character counter and content-warning label wrap awkwardly (121.2)

**Where:** Compose page, mobile viewport (375px), the row between the file input and the description text.
**Issue:** The "0/500" character counter and the "Content warning" checkbox label wrap to a second line and look disconnected from their controls. The "Note" and "Public" dropdowns also feel cramped.
**Expected:** On mobile, stack the character counter below the textarea, and give the content-warning checkbox its own row. Or use a more compact layout that fits in 375px.
**Severity:** Low — cosmetic, but makes the compose page look unfinished on mobile.

### 3. Notifications "Mark all as read" button placement (121.3)

**Where:** Notifications page, top area.
**Issue:** On mobile the "Mark all as read" button is centered between the page title and the filter pills, creating an awkward visual gap. On desktop it's right-aligned in the same row as the title, which looks fine.
**Expected:** On mobile, either right-align the button in the same row as the title (like desktop), or move it below the filter pills.
**Severity:** Low — cosmetic.

### 4. Empty home timeline: no guidance (121.4)

**Where:** Home timeline page, when the user has no follows.
**Issue:** The page shows only "Home timeline" with nothing below. No loading indicator, no empty-state message, no suggestion to follow people. A new user with no follows sees a blank page and may think the app is broken.
**Expected:** Show an empty state: "You haven't followed anyone yet. Find people to follow in the Directory or Communities." with a link to /directory.
**Severity:** Medium — onboarding friction.

### 5. Public timeline: "To followers" audience line is confusing for anonymous visitors (121.5)

**Where:** Public timeline (homepage), posts by local users.
**Issue:** Each post shows "TO followers" with a link to the actor's followers collection. For an anonymous visitor, this is meaningless — they can't view followers, and "followers" isn't a recognizable concept without context. The audience line adds noise without value.
**Expected:** Hide the "TO" line for anonymous users, or replace it with a more informative label like "visible to alice's followers". Alternatively, only show the audience line when it's a public-to-the-world post (as:Public) vs. followers-only.
**Severity:** Low — minor noise.

### 6. Actor profile: "Posts" section header and description are redundant (121.6)

**Where:** Actor profile page (both own profile at /profile and others at /actor?iri=...).
**Issue:** Below the "Posts" tab, there's a section header "Posts" again, followed by a description: "This actor's notes and articles. Social activities (follows, boosts, moderation) are filtered out." The repeated "Posts" label is redundant, and the description is implementation detail that most users don't need.
**Expected:** Remove the redundant "Posts" section header and the description text. The tab label is sufficient context.
**Severity:** Low — cosmetic.

### 7. Boosted posts: no visual distinction between local and remote (121.7)

**Where:** Public timeline and home timeline, boosted/announced posts.
**Issue:** A boosted post shows "Boosted by [actor]" followed by "View boosted post →". The original post's content is not shown inline — the user must click through to see it. For local posts this is fine (the user can guess the content), but for remote posts the user has no idea what was boosted without clicking.
**Expected:** Consider showing a truncated preview of the boosted content inline (like Mastodon does), or at least show the original author's name and a snippet.
**Severity:** Low — usability improvement, not a bug.

## Pages that look good (no issues found)

- **Public timeline (desktop + mobile):** Clean layout, good spacing, avatars render, timestamps are relative.
- **Profile (own, desktop + mobile):** Banner, avatar, tabs, posts all render well. Parent media in replies works.
- **Compose (desktop):** Clean, well-organized. Autocomplete works.
- **Directory (desktop + mobile):** Search box, People/Communities tabs, This instance/All known toggle, actor cards with Follow/Unfollow all work.
- **Communities (desktop + mobile):** Create form, community list, all clean.
- **Search (desktop + mobile):** Simple and clean.
- **Settings (desktop + mobile):** 3-tab structure works well, subsections expand/collapse, lazy loading works.
- **Actor profile (desktop + mobile):** Banner, avatar, moderation buttons, tabs, posts all good.
- **Object detail (desktop + mobile):** Post content, engagement buttons, reply section all clean.
- **Login/Register (desktop + mobile):** Clean forms, good spacing.

## New 121.* items created

1. **121.1** — Notifications: replace raw IRI links with friendly labels
2. **121.2** — Compose mobile: fix character counter and content-warning label wrapping
3. **121.3** — Notifications: fix "Mark all as read" button placement on mobile
4. **121.4** — Home timeline: add empty state when user has no follows
5. **121.5** — Public timeline: hide or improve "To followers" audience line for anonymous visitors
6. **121.6** — Actor profile: remove redundant "Posts" section header and description
7. **121.7** — Boosted posts: show inline content preview
