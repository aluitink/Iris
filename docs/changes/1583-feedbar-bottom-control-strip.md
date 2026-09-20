# 1583 — Phase 3: FeedBar bottom control strip

**Workstream:** Unified home feed (③④)
**Phase:** 3 of 7

## What was built

New `FeedBar.razor` component rendered in `MainLayout.razor` (signed-in branch, after the
auth-bar). The strip is sticky-bottom and mirrors the auth-bar's styling.

**Layout:**
- **Left** (`/home` only): `Posts` · `Communities` tab pills. Clicking a tab sets the active
  state and persists it to `localStorage` (`iris:homeTab`). The tabs are visual only in this
  phase — Phase 4 wires them to the feed's `?source=` filter.
- **Right** (all pages): `🔔` notification bell + `NotificationBadge` → `/notifications`.

**Visibility:**
- Signed-in: strip always visible (tabs on `/home`, bell everywhere).
- Signed-out: strip hidden (the auth-bar takes its place).

## Files changed

- `apps/Iris.Web.Client/Components/FeedBar.razor` — **new** component.
- `apps/Iris.Web.Client/Components/Layout/MainLayout.razor` — renders `<FeedBar />`.
- `apps/Iris.Web.Client/wwwroot/css/app.css` — `.feed-bar`, `.feed-bar-tabs`, `.feed-bar-bell` CSS.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (fresh browser context, `s7test`):
  - `/home` signed-in: FeedBar visible with Posts + Communities tabs + Notifications bell.
  - `/profile` signed-in: FeedBar shows only the bell (no tabs).
  - Signed-out: FeedBar hidden.
  - Tab switch: clicking "Communities" marks it `[active]`; state persists in localStorage.
  - 0 console errors.
