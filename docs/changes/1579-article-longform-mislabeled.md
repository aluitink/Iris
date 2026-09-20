# 1579 — S10: Article "(long-form)" mislabeled

**Severity:** S2
**Status:** Fixed

## Problem

The compose type selector showed "Article (long-form)" but the implementation had no title
field, no larger character limit, and no distinct long-form rendering — the Article was
stored and rendered identically to a Note. The "(long-form)" label was misleading.

## Fix

`Compose.razor`:
- Type selector option: "Article (long-form)" → "Article".
- Formatting tip: "Notes are short-form; Articles are for long-form content." →
  "Notes and Articles share the same character limit; Articles are stored as long-form objects."
- Compose hint (when Article is selected): "A note addressed to the public…" →
  "An article addressed to the public…" (type-aware).

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (fresh browser context):
  - Compose selector shows "Article" (not "Article (long-form)").
  - Formatting tip reads "Notes and Articles share the same character limit; Articles are
    stored as long-form objects."
  - Selecting Article updates the hint to "An article addressed to the public…".
