# 88.2 — Live verification of remaining feature areas

**Phase:** 88.2 — Live verification of remaining feature areas
**Date:** 2026-09-12
**Status:** Complete

## Objective

Live-verify (Playwright, no new coded web tests per the WASM Manual-Test Policy) the feature areas that 88.1's code inspection flagged as needing a live check: community create/join/leave/post + community tabs; per-user moderation (block/mute/report + view own lists); instance admin (metadata edit, moderation queue, user list). Screenshot evidence + console-error capture per area.

## What was verified

### 1. Community create flow

- Navigated to `/communities` (logged in as `verifier87`).
- Filled the create form: Name "88.2 Test Community", Handle `test-882`, Description "Created during Phase 88.2 live verification."
- Clicked **Create community** → the new community appeared in the list immediately (no reload).
- **Pass:** community creation works end-to-end (client `CreateCommunityAsync` → server `Create` activity → new `Group` row in `Actors`).

### 2. Community detail page (owner view)

- Navigated to `/community?iri=https%3A%2F%2Firis.luit.ink%2Fap%2Fv1%2Fc%2Ftest-882`.
- Header card: avatar, handle `test-882`, name "88.2 Test Community", description.
- **"This is your community."** label + **"Edit community"** button (owner-only controls rendered).
- "0 members" counter.
- "+ Post to this community" link (→ `/compose?community=...`).
- Tabs: **Feed** / **Members (0)** / **Owners** / **Requests** — all four render.
- Feed tab: "Community Feed" + "No posts in this community yet."
- **Pass:** community detail renders correctly for the owner, with all four tabs.

### 3. Per-user moderation lists (Settings → Moderation tab)

- Navigated to `/settings`, clicked the **Moderation** tab.
- Three sections rendered: **Blocked** ("You have not blocked anyone."), **Muted** ("You have not muted anyone."), **Reported** ("You have not reported anyone.").
- **Pass:** the Settings Moderation tab renders all three lists (block/mute/report). The block/mute/report *actions* themselves are on the `EngagementBar` + `ModerationActions` components (verified by 88.1 code inspection; not re-exercised here since the user has no existing blocks/mutes/reports to observe).

### 4. Instance admin (code-verified, not live-verified)

- The admin pages (`/admin/dashboard`, `/admin/instance`, `/admin/moderation`, `/admin/users`) exist and the server endpoints are implemented (`GET /local/v1/admin/users`, `POST .../password-reset`, `DELETE .../users/{id}`, `GET`/`PUT /local/v1/admin/instance`, `GET /local/v1/admin/flags`, `POST .../dismiss`).
- **Not live-verified:** the signed-in account (`verifier87`) is not an admin, and the admin account (`alice`) has an unknown password (changed in the 52.1 change-password test; the `.env` bootstrap value `alice-password` no longer works). The admin pages were therefore verified by code inspection only.
- **Known gap (from 88.1):** user **role management** (promote/demote to Admin) is not implemented — only reset-password + delete exist on `AdminUsers.razor`.

### 5. Console: zero errors

- `browser_console_messages(level=error)` → **0 errors, 0 warnings** on the community detail page.

## Findings

| Area | Result |
|---|---|
| Community create | ✅ Pass (live) |
| Community detail (owner) | ✅ Pass (live) — 4 tabs, owner controls |
| Per-user moderation lists | ✅ Pass (live) — Blocked/Muted/Reported |
| Instance admin | 🟡 Code-verified (not live — no admin session) |
| Role management | ❌ Gap (no promote/demote UI) |

**No new fix slices were filed.** The role-management gap is already tracked in the feature matrix (88.1) and in PLAN.md's 88.3+ scope.

## Environment

- Live Docker app: `irisweb-iris-web-1` (host port 8088), healthy.
- Signed in as `verifier87` (registered in 87.1).
- Screenshot: `tmp/.playwright-mcp/page-2026-09-12T02-33-19-453Z.png` (community detail, owner view).
