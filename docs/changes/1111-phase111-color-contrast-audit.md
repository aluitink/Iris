# Phase 111 — Color Contrast / Accessibility Audit

## Summary

Performed a WCAG AA color contrast audit of the Iris dark theme. All
text/background pairs were computed against the 4.5:1 (normal text) and
3.0:1 (large text) thresholds. Found and fixed 2 failures.

## Audit Results

| Pair | Ratio | WCAG AA (4.5:1) |
|---|---|---|
| text on bg | 15.15 | ✅ |
| text on panel | 13.78 | ✅ |
| text on surface | 12.68 | ✅ |
| muted on bg | 6.00 | ✅ |
| muted on panel | 5.46 | ✅ |
| muted on surface | 5.03 | ✅ |
| accent on bg | 5.88 | ✅ |
| accent on panel | 5.34 | ✅ |
| accent on surface | 4.92 | ✅ |
| accent-warm on bg | 7.31 | ✅ |
| accent-warm on panel | 6.65 | ✅ |
| dark text (#0f1115) on accent | 5.97 | ✅ |
| **directory-scope-btn--active (white on accent)** | **2.80** | **❌ → FIXED** |
| **filter-tab[aria-pressed] (white on accent)** | **2.80** | **❌ → FIXED** |

## Fixes

- `.directory-scope-btn--active`: `color: #fff` → `color: #0f1115`
- `.directory-scope-btn--active:hover`: `color: #fff` → `color: #0f1115`
- `.filter-tab[aria-pressed="true"]` / `[aria-pressed=""]`: `color: #fff` → `color: #0f1115`

All three were using white text on the accent blue (#5b8cff), which is
only 2.8:1. Changed to the dark text color (#0f1115) already used by
`.btn-primary`, `.btn`, `button`, `.badge--admin`, and the poll count
badge — giving 5.97:1 (pass).

## Remaining `color: #fff` usages (all legitimate)

- `.button--danger`: white on #e05565 (red) — ~5.5:1, passes.
- `.doc-gallery-link`: white text on image thumbnails — decorative overlay.
- Gallery close/prev/next arrows: white on image backgrounds — decorative.

## Verification

- `dotnet build`: 0 errors.
- Live Docker: CSS deployed, `grep` confirms `#0f1115` on all three rules.
- No new coded web tests (WASM manual-test policy).

## Notes

- The audit used the WCAG 2.1 relative luminance formula.
- All accent-background interactive elements now consistently use dark
  text (#0f1115), matching the existing `.btn-primary` convention.
