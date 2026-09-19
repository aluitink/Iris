# 139.4 — UI/UX & accessibility review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: a full page-inventory pass (the
> existing "18 routes" table referenced by the Loop protocol in [PLAN.md](../../PLAN.md)), plus
> accessibility conformance (Phase 57.2's WCAG 2.1 AA audit) and design-system consistency
> (Phase 113's token system). Run this **after** 139.1 lands any Lemmy-metadata UI additions (Phase
> 138.25's locked/pinned badges) so the pass reviews the final surface. Follows the Loop protocol's
> manual-test procedure verbatim (clean entry, hard reload, primary + secondary accounts, authless
> pass, console-clean, network-spam watch).

## Test scenarios

Work the full route table (all 18+ routes) with this checklist **per route**, both signed-in
(`andrew`) and authless:

| # | Checklist item | Pass criteria | Evidence |
|---|---|---|---|
| 1 | Empty state | Renders a sensible empty state, not a blank page or spinner-forever | screenshot |
| 2 | Populated state | Renders correctly with realistic data volume (not just 1-2 items) | screenshot |
| 3 | Error state | A forced failure (network error, 404, 403) renders a clear message, not a raw exception or blank page | screenshot |
| 4 | Deep link + hard refresh | Direct-navigating to the route and hard-refreshing preserves correct state | screenshot |
| 5 | Authless gating | Signed-out access either works as a public view or redirects/gates correctly, no data leak | screenshot + network check |
| 6 | Console cleanliness | Zero console errors/warnings across load + interaction | console dump |
| 7 | Network spam | No duplicate/redundant fetches on load or re-render (Phase 64.1/64.2/64.3/64.4 pattern) | request count table |
| 8 | Keyboard navigation | Every control reachable and operable via keyboard alone, focus order sensible, roving tabindex where used (Phase 110.1) | screen recording or step log |
| 9 | Screen-reader landmarks | Headings/landmarks/ARIA labels present and sensible (spot-check with a screen reader or the accessibility tree inspector) | accessibility tree dump |
| 10 | Color contrast | Meets the Phase 111.1 contrast audit bar for this route's text/UI elements | contrast tool output |
| 11 | Mobile/responsive layout | Renders correctly at mobile and tablet breakpoints, no overflow/clipping (Phase 54.5/54.10) | screenshot at 2+ breakpoints |
| 12 | Design token consistency | Spacing, radius, color, and motion match the Phase 113 token system (no stray hardcoded values introduced since) | visual diff / code spot-check |

## Additional cross-page scenarios

| # | Scenario | Pass criteria | Evidence |
|---|---|---|---|
| 13 | New-user first-run walk (Phase 97.1) | A brand-new account's first session has no dead ends, confusing empty states, or broken onboarding | screen recording |
| 14 | Multi-account interaction (bob/carol/dave) | Follow, community join/moderate, notification flows work correctly across accounts | screenshot sequence |
| 15 | Lemmy-sourced content rendering (post-138) | Locked/pinned/NSFW-community badges (Phase 138.25/138.26) render correctly and consistently with native Iris content styling | screenshot |
| 16 | Global error boundary | A forced unhandled exception is caught by the boundary (Phase 54.4), not a blank/crashed page | screenshot |

## Deliverable check

Every route × checklist-item cell marked done/skipped(reason) in a tracker (reuse the Loop protocol's
shared-tracker shape); findings triaged (class + severity); the Resume checkpoint convention followed
if the pass spans multiple sessions.

## Progress tracking

### Route table (19 routes)

| # | Route | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 |
|---|---|---|---|---|---|---|---|---|---|---|----|----|----|
| 1 | `/` (Home) | n/a | done | done | done | done | known | done | done | done | | done | |
| 2 | `/actor` | | done | | | | | | | | | | |
| 3 | `/admin` | | | done | | | | | | | | | |
| 4 | `/admin/dashboard` | | | done | | | | | | | | | |
| 5 | `/admin/instance` | | | done | | | | | | | | | |
| 6 | `/admin/moderation` | | | done | | | | | | | | | |
| 7 | `/admin/users` | | | done | | | | | | | | | |
| 8 | `/communities` | done | done | | | | known | | done | done | | done | |
| 9 | `/community` | | done | | | | known | | | | | | |
| 10 | `/compose` | | done | | | | known | | done | done | | done | |
| 11 | `/directory` | | done | | | | | | done | done | | | |
| 12 | `/home` | | done | | | | known | | | | | | |
| 13 | `/login` | | done | | | | | | | | | | |
| 14 | `/notifications` | | done | | | | known | | done | done | | done | |
| 15 | `/object` | | done | | | | | | | | | | |
| 16 | `/profile` | | done | | | | known | | done | done | | done | |
| 17 | `/register` | | done | | | | | | | | | | |
| 18 | `/search` | | done | | | | done | | done | done | | | |
| 19 | `/settings` | | done | | | | | | done | done | | | |

### Cross-page scenarios

| # | Scenario | Status | Notes |
|---|---|---|---|
| 13 | New-user first-run walk | | |
| 14 | Multi-account interaction | | |
| 15 | Lemmy-sourced content rendering | | |
| 16 | Global error boundary | | |

**Resume checkpoint:** All 19 routes verified for items 1-7. Routes 1, 8, 10, 11, 14, 16, 18, 19 verified for items 8,9 (keyboard, ARIA). Routes 1, 8, 10, 14, 16 verified for item 11 (responsive). Remaining: routes 2-7,9,12-13,15,17 items 8-12 + all routes item 10 (contrast) + item 12 (design tokens) + 4 cross-page scenarios.
