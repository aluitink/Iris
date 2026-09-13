# 123.3 — Community detail: mobile layout for ownership banner

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Problem

On mobile (375px), the community detail page's ownership banner — "This is your community." text + "Edit community" button + "0 members" count — all competed for space in a single horizontal flex row and wrapped awkwardly. The button got squeezed between the two text fragments.

## Fix

Added a mobile media query rule for `.community-detail-actions` inside the existing `@media (max-width: 768px)` block:

```css
.community-detail-actions {
    flex-direction: column;
    align-items: flex-start;
    width: 100%;
}
```

This stacks the items vertically on small screens: text on one line, button below, count below that. Desktop layout (horizontal row) is unchanged.

### Files changed

- `apps/Iris.Web.Client/wwwroot/css/app.css` — added the mobile rule
- `apps/Iris.Web/wwwroot/css/app.css` — synced (both copies must stay in sync)

## Verification

- `dotnet build` clean (0 warnings, 0 errors).
- `dotnet test` green: 1,823 pass, 0 fail (1 known flaky federation test passes when server suite runs alone).
- Live-verified (Playwright, Docker app):
  - Mobile (375px): "This is your community." / "Edit community" / "0 members" stack vertically.
  - Desktop (1400px): horizontal row unchanged.
  - 0 console errors.
