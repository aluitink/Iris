# 63.1 — UI/UX visual & usability review (tracker)

Phase 63.1: a route-by-route visual + usability pass over the production Blazor
WASM app, logged here. Class `ux`. Severity: **low** (cosmetic/affordance),
**med** (usability/a11y gap), **high** (misleads or blocks a task). Route
performance / request-spam findings are logged to the **64** request-spam
tracker, not here.

## Method

- Live app on `:8089` (same-origin, DB-backed), signed in as `andrew`.
- Computed-style + WCAG-contrast analysis (pixel screenshots unavailable in the
  MCP harness — see 620 tracker methodology).
- Native-DOM clicks / `form.submit()` for any state changes.
- Responsive spot-checked at 1280px (desktop) and 390px (mobile).

## Findings

| # | Route | Finding | Severity | Detail | Status |
|---|-------|---------|----------|--------|--------|
| U-01 | Global (`a`) | Default link has no persistent underline; relies on color alone | low | Base rule `a { color: var(--accent-warm) }` (line 211) sets no underline at rest. Most link contexts override this and are fine: `.object-content a` (green + underline), `.actor-card-handle` (bold 700 + hover color), `.object-hashtag-link` (accent + bold), tabs (active-state). But any plain `a` that falls back to the base rule is signaled by color only (WCAG 1.4.1) until hover. **Fix**: added `text-decoration: underline` + `text-underline-offset: 2px` to the base `a` rule (both app.css copies); contexts that want no underline already override it. Verified live: "Create an account" link now underlined + accent. `a:focus-visible` strong (3px accent outline). | fixed |
| U-02 | Login / Register | Form-label style inconsistent with the rest of the app | low | Auth pages use `.field label` (normal case, weight 400, 0.85rem, muted). Every other form (Communities create, Settings, Compose) uses `.form-group label` (uppercase, weight 600, 0.03em letter-spacing). Same size + color, different case/weight → looks like two different design systems. **Fix**: `.field label` now `font-weight:600; text-transform:uppercase; letter-spacing:0.03em; margin-bottom:0.35rem` to match `.form-group label` (both app.css copies). Verified live: auth "Handle" label now uppercase/600/letter-spaced. | fixed |

## Reviewed — no finding

- **Home**: dark theme `#111318`/`#e6e8ec`, 16px/1.6. Contrast all ≥ 6:1 (body 15.15:1, muted 6.0:1; 0 elements < 3:1). Cards 8–10px radius, `#262b36` borders. 32px circular avatars. "Load more" = consistent secondary button. No horizontal overflow.
- **Search**: centered muted empty state ("No matches found. / Try a different handle…"), primary blue submit, flex row form. Good.
- **Communities**: create form (uppercase labels, consistent 6px inputs) + 2 community cards.
- **Profile**: 64px avatar, tab bar with a clear active state (filled `#1f242e` bg on the active tab). `main` = `max-width:720px; margin:0 auto` (centers at all widths).
- **Settings**: 6 tabs (Account/Notifications/Communities/Relays/Moderation/Danger). Danger tab = clear irreversible copy + red destructive button (`#e05565`, white text). Change password = primary blue.
- **Directory**: People/Communities tab switcher, cards with follow/unfollow, "Show posts by X" aria-labels. (Two `alice` rows = known dev-data duplicate actor, not a defect.)
- **Mobile (390px)**: nav collapses to an aria-labelled "Menu" toggle that opens all 9 links; `main` padding tightens to `16px 12px`; no horizontal overflow; nothing spills the viewport.

## Notes / deferred

- U-01 (base-`a` underline) is a one-line CSS change; U-02 (auth label) is a
  3-line `.field label` tweak. Both fixed in-slice for 63.1.
- The duplicate `alice` actor (localhost:8088 IRI) in the dev DB is data, not code —
  do not "fix" in the app.
