# 121.3 — Notifications: fix "Mark all as read" button placement on mobile

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Scope

On mobile (≤768px) the notifications page header stacks vertically (`flex-direction: column`). The "Mark all as read" button was centered rather than right-aligned, creating an awkward visual gap between the title and the filter pills.

## Change

**Files:** `apps/Iris.Web.Client/wwwroot/css/app.css` + `apps/Iris.Web/wwwroot/css/app.css` (kept in sync)

Added one rule inside the existing `@media (max-width: 768px)` block:

```css
.page-header-row .btn {
    align-self: flex-end;
}
```

In a `column` flex container, `align-self: flex-end` pushes the button to the right edge of the cross axis. Desktop (single-row `space-between` layout) is unaffected.

## Verification

- **Build:** 0 warnings, 0 errors.
- **Web tests:** 88/88 pass.
- **Live (375×812):** "Mark all as read" button is right-aligned in the stacked header row.
- **Live (1400×900):** button remains right-aligned in the same row as the title (unchanged).
- **Console:** 0 errors.

## Files changed

- `apps/Iris.Web.Client/wwwroot/css/app.css` — +5 lines
- `apps/Iris.Web/wwwroot/css/app.css` — +5 lines
