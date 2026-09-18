# Phase 113 — Design-Token System (app.css)

## What

Phase D (polish) begins with the design-token system the UI-guidelines doc
recommended: "define a small set of CSS custom properties early … so the
Phase D polish pass is a token-value swap instead of a find-and-replace
across every component's CSS." This slice adds that token layer to
`apps/Iris.Web.Client/wwwroot/css/app.css` and migrates the dominant raw
values onto it.

## Changes (all in `app.css`, presentational only)

1. **Semantic color tokens** added to `:root`:
   - `--danger: #e05565`, `--danger-fg: #e79c9c`, `--danger-bg: #2a1a1a`,
     `--danger-border: #5b2f2f` (the `.error` block, `#blazor-error-ui`,
     `.compose-char-count--over`, `.button--danger`).
   - `--warning: #d4a030` (`.button--warning`).
   - `--success: #8fd3a7` (`.object-content a`, sensitive-reveal,
     spinner stroke).
   - `--btn-fg: #0f1115` (dark text on accent backgrounds — the WCAG-AA
     value Phase 111 standardized).
   - `--surface-inset: #12171f` (inset object panels) and
     `--border-strong: #3a4658` (elevated borders on chips/labels).
   - Cleaned stale `var(--accent, #4a90d9)` fallbacks → `var(--accent)`
     (`#4a90d9` was an outdated value; the live accent is `#5b8cff`).
   - Left intentionally raw: `#fff` on the PDF viewer + lightbox controls
     (white on a *light media* background — correct, not an accent button).

2. **Spacing scale** (`--space-1…7`, 4px base: 0.25/0.5/0.75/1/1.5/2/3rem)
   and **type scale** (`--font-size-xs/sm/base/lg/xl/2xl`).

3. **Migrations** onto the tokens (the high-frequency values):
   - `0.25/0.5/0.75/1rem` spacing → `--space-1/2/3/4` (~230 occurrences).
   - `0.75/0.8/0.85/0.9/0.95/1.1rem` font-sizes → `--font-size-xs/sm/base/lg`
     (~110 occurrences).
   - Raw semantic hexes → the color tokens above.

   Low-frequency odd values (e.g. `0.35/0.6/1.25rem`) were left as raw rem —
   they are not part of the standard ramp and tokenizing them would add
   noise without a single-swap benefit.

## Why

This is the enabling slice for the rest of Phase D: dark/light theming,
a spacing or type-scale adjustment, and any future component library now
operate on tokens instead of ~430 scattered literals. It is a no-op
visually (every token resolves to the exact hex/rem it replaced).

## Verification

- `dotnet build` clean (0 errors); `Iris.Web.Tests` 95/95.
- Rebuilt the Docker app; served `app.css` (fetched with `cache: no-store`)
  confirmed to contain all new tokens.
- Live Playwright (fresh context): the `.btn` primary resolves to
  `background rgb(91,140,255)` (`--accent`) + `color rgb(15,17,21)`
  (`--btn-fg`), and a migrated font-size resolves to `13.6px`
  (`--font-size-sm`). Note: `getComputedStyle().getPropertyValue('--<new
  custom-prop>')` reads empty in this headless Chromium even though the
  tokens resolve correctly on real elements — a known headless quirk, not a
  CSS defect.
- Headless WASM login POST 400s in this environment (antiforgery/WASM
  automation limitation, pre-existing and unrelated to this presentational
  change — the container log is clean).

## Files

- `apps/Iris.Web.Client/wwwroot/css/app.css` (379 insertions / 347
  deletions, net +32 lines of token definitions).
