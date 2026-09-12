# Phase 113.2 — Visual Consistency + Subtle Motion Pass

## What

A focused Phase D polish pass over `apps/Iris.Web.Client/wwwroot/css/app.css`
(continuing the design-token work from 113.1): consistent hover/motion on the
primary interactive elements, an accessibility contrast fix on the danger
button, and cleanup of stale `var()` fallbacks left over from earlier phases.

## Changes (all in `app.css`, presentational only)

### Motion (subtle, 0.15s)
- Primary buttons (`button, .button`, `.btn`) now carry a
  `transition: filter, background, color, border-color 0.15s ease` so the
  existing `brightness(1.1)` hover fades in instead of snapping.
- Links (`a`) get `transition: color 0.15s ease` for the hover color shift
  (accent-warm → accent).
- Secondary buttons (`.button--secondary`, `button.secondary`) get a proper
  hover state: `background: var(--surface)` + `border-color:
  var(--border-strong)`, with `filter: none` (they were previously only
  inheriting the brightness filter, which looks wrong on a dark panel bg).

### Accessibility
- `.button--danger` text changed from `#fff` (2.8:1 on the danger red —
  fails WCAG AA) to `var(--btn-fg)` (`#0f1115`, the same dark-on-accent value
  Phase 111 standardized). Added an explicit `.button--danger:hover`.
- `prefers-reduced-motion: reduce` extended from the splash-only guard to a
  global `transition-duration: 0.01ms !important` + `animation-duration:
  0.01ms !important` + `scroll-behavior: auto` on all elements (WCAG 2.3.3).

### Token consistency
- `.engagement-btn:hover` background: raw `rgba(143,211,167,0.1)` →
  `color-mix(in srgb, var(--success) 10%, transparent)` so it tracks the
  success token.
- Removed stale `var()` fallbacks that referenced either undefined tokens
  (`--text-muted`) or outdated values (accent `#4a90d9`/`#7c6cf0`, light-mode
  `#f5f5f5`/`#1a1a2e`, `rgba(74,144,217,.12)`): now plain `var(--text)` /
  `var(--border)` / `var(--accent)` / `var(--muted)` / `var(--danger)` /
  `var(--hover)` / `var(--bg)` / `var(--surface)`. All remaining undefined
  `var()` names (e.g. `--bg-elevated`, `--radius`) still carry their own
  fallbacks and are untouched.

## Why

Buttons and links are the highest-frequency interactive elements; giving them
consistent, subtle motion is the cheapest high-impact polish. The danger-button
text fix closes the last white-on-accent AA gap. The fallback cleanup removes
the confusion of a token system with two "accent" values and a light-mode
palette hiding in fallbacks.

## Verification

- `dotnet build` clean (0 errors); `Iris.Web.Tests` 95/95.
- Rebuilt the Docker app. Live Playwright (fresh context): served `app.css`
  confirmed to contain the button/link transitions, the secondary hover,
  `--btn-fg` on danger, the global reduced-motion guard, and the `color-mix`
  success hover; computed `transition-duration` on the primary `.btn` =
  `0.15s, 0.15s, 0.15s, 0.15s`, link `transition-property` = `color`. Login
  page screenshot renders correctly (accent button, dark text, no layout
  regression).

## Files

- `apps/Iris.Web.Client/wwwroot/css/app.css` (42 insertions / 20 deletions).
