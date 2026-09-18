# Phase 110 — Roving Tabindex + Arrow-Key Navigation for Tab Bars

## Summary

Added WAI-ARIA-compliant keyboard navigation for all `role="tablist"` tab
bars: roving tabindex (active tab `tabindex=0`, others `-1`) and
ArrowRight/ArrowLeft/ArrowUp/ArrowDown + Home/End key handling with
automatic tab activation.

## Changes

### index.html (JS only — no C# changes)

Added a self-contained IIFE in `apps/Iris.Web.Client/wwwroot/index.html`
that:

- **Roving tabindex:** For each `role="tablist"`, sets `tabindex="0"` on the
  `aria-selected="true"` tab (or the first tab if none selected) and
  `tabindex="-1"` on all others. This gives a single keyboard entry point
  per tab bar, per the WAI-ARIA tabs pattern.
- **Arrow-key navigation:** `keydown` listener on each tab list:
  - `ArrowRight` / `ArrowDown` → next tab (wraps)
  - `ArrowLeft` / `ArrowUp` → previous tab (wraps)
  - `Home` → first tab, `End` → last tab
  - Moves focus to the target tab and calls `.click()` (automatic activation).
- **Re-sync:** A `MutationObserver` on `aria-selected` re-applies roving
  tabindex whenever the active tab changes (e.g. after a click or
  programmatic `SwitchTab`).
- **Progressive init:** Tab lists render as pages load, so a 1-second
  interval scan (30 s cap) initializes each new `role="tablist"` it finds
  (marked with `data-iris-tab-init` to avoid double-init).

### Matrix

- **`docs/plans/production-app-feature-matrix.md`**: Keyboard navigation
  🟡 → ✅ (roving-tabindex + arrow-key handling now present on all 5 tab
  bars: Settings, Profile, ActorDetail, Directory, CommunityDetail).

## Verification

- `dotnet build`: 0 errors, 0 warnings.
- Live Playwright (fresh context, WASM hash `xhajlhmxb4`):
  - Settings tab bar: `data-iris-tab-init="true"` after 1 s.
  - Tabindex: `tab-account` (active) = `0`, all others = `-1`. ✓
  - `ArrowRight` from Account → focus moves to `tab-notifications`. ✓
  - No console errors.

## Notes

- Pure JS, zero C# surface area. The existing `@onclick:preventDefault
  @onclick='() => SwitchTab(...)'` on each tab handles the click activation;
  the JS layer only manages focus + tabindex.
- The 5 tab bars (Settings, Profile, ActorDetail, Directory,
  CommunityDetail) all use the same `role="tablist"` / `role="tab"` markup,
  so a single global script covers them all.
- No new coded web tests (WASM manual-test policy).
