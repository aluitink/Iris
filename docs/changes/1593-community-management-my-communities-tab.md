# ④ Community management page — "My communities" tab

- **Date:** 2026-09-20
- **Status:** done (partial — Phase 5 of community-simplification)
- **Plan:** [docs/plans/community-simplification.md](../plans/community-simplification.md) Phase 5

## Problem

The `/communities` page had only two tabs: "Following" (communities the user follows) and "All on
this instance" (the directory). There was no way to quickly see and manage the communities the user
**owns** — the user had to hunt through the "All" list and identify which ones they created.

## Fix

- **`apps/Iris.Web.Client/Components/Pages/Communities.razor`** — Added a third tab, "My
  communities", between "Following" and "All on this instance". The tab shows communities whose
  `attributedTo` includes the signed-in actor's IRI (i.e. communities the user owns/created). Each
  owned community card shows the standard Follow/Leave button + a Delete button (with confirmation),
  matching the existing delete flow. The tab reuses the already-loaded local search results (no extra
  API call).

## Verification

- 1392 server tests passed (0 failed)
- Build clean (0 warnings, 0 errors)
- Live-verified (s7test, fresh browser):
  1. Navigated to `/communities` → three tabs visible: Following, **My communities**, All on this instance.
  2. Clicked "My communities" → shows the two communities s7test owns (`s21-second`, `s21-verify`)
     with Leave + Delete buttons.
  3. 0 console errors.

- **`apps/Iris.Web.Client/Components/Pages/Communities.razor`** (continued) — Each owned community
  card in the "My communities" tab now shows a "Manage peers →" link that navigates to the community
  detail page's Peers tab (`/community?iri=…#peers`), where the full add/remove peer UI lives. This
  gives the owner a one-click path from the management overview to the per-community peering surface
  (per the plan's resolved question: "keep both the management-page Peers section and the detail-page
  Peers tab" — the management page is the overview, the detail tab is the deep surface).

- **`apps/Iris.Web.Client/wwwroot/css/app.css`** + **`apps/Iris.Web/wwwroot/css/app.css`** — Added
  `.manage-peers-link` style (inline-block, small font, top margin).

## Verification

- 1392 server tests passed (0 failed)
- Build clean (0 warnings, 0 errors)
- Live-verified (s7test, fresh browser):
  1. `/communities` → "My communities" tab → two owned communities shown.
  2. Each card has Leave + Delete + **"Manage peers →"** link.
  3. Clicked "Manage peers →" on `s21-second` → navigated to
     `/community?iri=…#peers` → Peers tab visible with add-peer form + "This community follows no one yet."
  4. 0 console errors.

## Remaining (next slice)

- "Leave (co-owner)" flow for non-last owners (currently only Delete is shown; the Follow/Leave
  button already handles the follow edge, but a co-owner who is not the last owner needs an explicit
  "Remove myself as owner" action).
