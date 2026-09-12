# Phase 115.4 — Moderation (per-user): Live Verification + Matrix Reconciliation

## What

Live-verified the per-user moderation web integration and reconciled its
D-column (web-integration) matrix entries. The feature was **already fully
implemented**; this slice closes the verification gap and updates the feature
matrix.

## What was verified (Playwright, signed in as alice)

### Block
- `/actor?iri=…` (external actor Gargron): **Block** button present alongside
  Mute + Report. Clicked Block → button toggled to **Unblock**.
- DB confirmed: `Edges` row Kind=5 (Block), Source=alice, Target=Gargron.

### Mute
- `/actor?iri=…` (external actor Marius): **Mute** button present. Clicked
  Mute → button toggled to **Unmute**.
- DB confirmed: `Edges` row Kind=7 (Mute), Source=alice, Target=Marius.

### Flag / report
- `/actor?iri=…` (external actor ScottEdelman): **Report** button present.
  Clicked Report → flag delivered (button stays visible; flags don't toggle
  UI state).
- DB confirmed: `Edges` row Kind=6 (Flag), Source=alice, Target=ScottEdelman.

### View own blocks/mutes/flags list
- `/settings` → Moderation tab: three sections render:
  - **Blocked**: Gargron (1 item)
  - **Muted**: Marius (1 item)
  - **Reported**: ScottEdelman (1 item)
- Each section has per-item action buttons (Unblock / Unmute / Remove).

## Matrix reconciliation (D-column)

All four Moderation (per-user) rows flipped ☐ → ✅:

| Item | Before | After |
|---|---|---|
| Block | ☐ | ✅ |
| Mute | ☐ | ✅ |
| Flag/report | ☐ | ✅ |
| View own blocks/mutes/flags list | ☐ | ✅ |

## Why a reconciliation slice

A and C were already ✅ with integration coverage. D was ☐ because the web
integration had never been live-verified and the matrix never reconciled. This
turn does exactly that — no new functional code.

## Verification

- `dotnet build` clean; `Iris.Server.Tests` 1105/0; `Iris.Web.Tests` 95/95.
- Live Playwright verification as above (Block/Mute/Report toggles + DB edges,
  Settings Moderation tab with all three lists). No console errors (one
  unrelated 401 from a proxied external resource).

## Files

- `docs/plans/production-app-feature-matrix.md` (4 D-column rows reconciled).
