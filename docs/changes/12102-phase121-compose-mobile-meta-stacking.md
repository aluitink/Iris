# 121.2 — Compose mobile: fix character counter and content-warning label wrapping

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Scope

On a 375px viewport the compose meta row (char counter, type select, visibility select, content-warning checkbox) wrapped awkwardly: the "0/500" counter and the "Content warning" label broke to a second line and looked disconnected from their controls.

## Change

**Files:** `apps/Iris.Web.Client/wwwroot/css/app.css` + `apps/Iris.Web/wwwroot/css/app.css` (kept in sync)

Added three rules inside the existing `@media (max-width: 768px)` block:

```css
.compose-meta {
    flex-wrap: wrap;
    justify-content: flex-start;
}

.compose-char-count {
    order: -1;
    flex-basis: 100%;
}

.compose-sensitive-toggle {
    flex-basis: 100%;
}
```

- `.compose-meta` now wraps on mobile and left-aligns.
- `.compose-char-count` gets `order: -1` (renders first) + `flex-basis: 100%` (its own row).
- `.compose-sensitive-toggle` gets `flex-basis: 100%` (its own row).
- The type + visibility selects share the middle row.

Desktop (≥769px) is unaffected — the base `.compose-meta` rule (single-row flex) still applies.

## Verification

- **Build:** 0 warnings, 0 errors.
- **Web tests:** 88/88 pass.
- **Live (375×812):** "0/500" on its own line, "Note"/"Public" selects on the next line, "Content warning" checkbox on its own line. No awkward wrapping.
- **Live (1400×900):** all four items in a single right-aligned row (unchanged).
- **Console:** 0 errors.

## Files changed

- `apps/Iris.Web.Client/wwwroot/css/app.css` — +16 lines (mobile media query additions)
- `apps/Iris.Web/wwwroot/css/app.css` — +16 lines (same)
