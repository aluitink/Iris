# Phase 113.3 — Complete Design-Token Spacing/Type Migration

## What

Completes the design-token system started in 113.1 by migrating the remaining
raw `rem` spacing values (and the heading display font-sizes) onto the token
scales. After this, `app.css` has **zero raw `rem` spacing values** — every
`margin`/`padding`/`gap`/`top`/`bottom`/`left`/`right` uses a `--space-*`
token, and the standard font-sizes use `--font-size-*` tokens.

## Changes (all in `app.css`, presentational only)

### Scale extensions
- **Spacing** (`:root`): added the off-ramp values that were in use but not on
  the 4px base ramp — `--space-25: 0.2rem`, `--space-35: 0.35rem`,
  `--space-45: 0.45rem`, `--space-55: 0.55rem`, `--space-60: 0.6rem`,
  `--space-65: 0.65rem`, `--space-90: 0.9rem`, `--space-125: 1.25rem`.
- **Type** (`:root`): added heading sizes `--font-size-3xl: 2rem`,
  `--font-size-4xl: 2.5rem`.

### Migrations
- All remaining raw `rem` spacing values → the matching `--space-*` token
  (~150 more occurrences). A negative offset (`right: -0.5rem`) became
  `calc(-1 * var(--space-2))`.
- `font-size` values `0.875rem`→`--font-size-sm`, `2.5rem`→`--font-size-4xl`,
  `2rem`→`--font-size-3xl`, `1.35rem`→`--font-size-xl`, `1.6rem`→
  `--font-size-2xl`.

### Left raw (intentional)
- A handful of one-off display/heading font-sizes (`0.65`–`1.8rem`, ~15
  occurrences across distinct components) were left as raw `rem` — they are not
  part of a repeatable ramp, and promoting each to a token would bloat the
  scale without a single-swap benefit.
- `width`/`height`/`border-radius`/`font-size` `rem` values were intentionally
  **not** touched (out of scope for the *spacing* token scale).

## Why

113.1 tokenized the high-frequency values; this finishes the job so the
"spacing change = single value swap" guarantee holds for the whole stylesheet,
not just the common cases. It also removes the last surprise: a reader of
`app.css` no longer has to wonder whether a `0.6rem` padding is a typo or a
deliberate off-ramp.

## Verification

- `dotnet build` clean (0 errors); `Iris.Web.Tests` 95/95.
- No corruption: a `digit.var(--…)` scan is clean (an early pass briefly turned
  `0.2rem`→`0.var(…)` via a substring match; all were restored and re-verified).
- Rebuilt the Docker app. Live Playwright (fresh context): the served `app.css`
  contains **no raw `rem` spacing values** (regex scan after stripping the
  `:root` token definitions returns none), all new scale tokens are present, and
  the `calc(-1 * var(--space-2))` negative offset is in place. Login page
  screenshot is pixel-identical to before (no layout regression).

## Files

- `apps/Iris.Web.Client/wwwroot/css/app.css` (165 insertions / 153 deletions).
