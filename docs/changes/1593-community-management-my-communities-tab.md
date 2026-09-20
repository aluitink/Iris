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

## Remaining (next slice)

- Peers section per owned community (view/add/remove community-follows) — either inline on this page
  or as a link to the existing Peers tab on `CommunityDetail.razor`.
- "Leave (co-owner)" flow for non-last owners (currently only Delete is shown).
