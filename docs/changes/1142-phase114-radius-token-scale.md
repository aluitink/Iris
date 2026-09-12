# Phase 114.2 — Coherent Border-Radius Token Scale

## What

Established a single border-radius design-token ramp and migrated the 41 raw
`border-radius` pixel values in `app.css` onto it. Before this, corner radii
were scattered across **six** different values (2, 4, 5, 6, 8, 10px) with no
shared scale — a card family could be 8px while an adjacent one was 10px, and
there was no way to change the app's corner rhythm in one place.

## The scale

| Token | Value | Use |
|---|---|---|
| `--radius-sm` | 4px | small controls, inputs, tags, chips |
| `--radius-md` | 8px | list-item cards (posts, actors, directory items, tab bar) |
| `--radius-lg` | 10px | outer content cards (`.card`, detail panels) |
| `--radius-pill` | 999px | full rounding (avatars, pills) |

## Migration

- 41 raw `border-radius: Npx` values → the matching token.
- **Card rhythm is now explicit:** outer cards `--radius-lg`, list-item cards
  `--radius-md`, controls `--radius-sm`. This is a two-tier card system (outer
  10px / inner list-item 8px) plus a control tier (4px) — the visual hierarchy
  that was previously accidental is now intentional and one-swap.
- The 5px value (`.card-moderate-btn`) and stray 6px/4px controls all collapsed
  onto `--radius-sm`.

## Left raw (intentional)

- `2px` — the `[tabindex]:focus-visible` outline ring (a focus indicator, not a
  corner).
- `50%` — avatar circles.
- `6px 0 0 6px` / `0 6px 6px 0` — the split multi-corner corners on paired
  input/button groups (not a single-radius case).
- `0.375rem` (`.object-poll`) and two `0.3rem` one-offs — not on the card
  rhythm; promoting them would bloat the scale without a single-swap benefit.

## Why

Continues the Phase 113–114 design-token program (colors → spacing → type →
radius). Radius was the last dimensional property left as raw pixels, so a
theme or rhythm change (e.g. "make corners sharper across the app") previously
required hunting ~41 sites. Now it's a one-line token change.

## Verification

- `dotnet build` clean (0 errors, warnings-as-errors on).
- `Iris.Web.Tests` 95/95 (no test asserts border-radius).
- Rebuilt the Docker app. Live Playwright (fresh context): the served `app.css`
  has all 4 radius tokens and 41 `var(--radius-*)` usages; the only remaining
  raw px radii are the 3 intentional ones (2px focus ring, multi-corner input
  corners). On the live login page, `.card` computes to `10px` and `.button` to
  `4px` — confirming the tokens resolve on real elements, not just in the file.

## Files

- `apps/Iris.Web.Client/wwwroot/css/app.css` (51 insertions / 41 deletions).
