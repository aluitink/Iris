# Phase 114.3 — Primary Card-List Spacing Rhythm

## What

Unified the inter-item gap across the app's primary bordered-card lists. The
audit found the card *containers* were already consistent (list-item cards all
use `--space-3 --space-4` padding + `--radius-md`), but the **gap between the
cards** was not:

| List | Container | Gap before | Gap after |
|---|---|---|---|
| Posts | `ul.object-list li` (margin-bottom) | `--space-3` (12px) | `--space-3` |
| Community cards | `.directory-cards` | `--space-3` (12px) | `--space-3` |
| Actors | `.actor-list-grid` | `--space-2` (8px) | **`--space-3`** |
| Communities | `.directory-list` | `--space-2` (8px) | **`--space-3`** |

Two 8px outliers were promoted to 12px so every top-level card list shares the
same rhythm.

## Why

Continues the Phase 113–114 design-token/consistency program. A "card list" is
a core layout primitive; its internal rhythm should be identical regardless of
what's in the cards (posts, actors, communities). The 8px/12px split was
accidental, not a deliberate hierarchy — all four are the same tier (top-level
bordered cards in a column).

## Hierarchy preserved

Nested / compact content keeps the tighter `--space-2` (8px), which *is* the
correct second tier:
- `.directory-card-posts` (posts inside an expanded directory card)
- `.compose-poll` (poll options inside the composer)
- `.actor-detail-actions` (action button rows)

So the rhythm is now: **top-level card lists = 12px, nested content = 8px** —
a clean two-tier system.

## Verification

- `dotnet build` clean (0 errors, warnings-as-errors on).
- `Iris.Web.Tests` 95/95 (no test asserts list gaps).
- Rebuilt the Docker app. Live Playwright (fresh context): the served CSS
  confirms all four primary lists use `--space-3` (`.actor-list-grid`,
  `.directory-list`, `.directory-cards`, `ul.object-list li`); the login page
  renders intact.

## Files

- `apps/Iris.Web.Client/wwwroot/css/app.css` (2 insertions / 2 deletions).
