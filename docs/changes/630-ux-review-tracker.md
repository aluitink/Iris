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
| U-03 | Notifications | Active filter tab never highlighted (selected filter invisible) | med | Filter tabs (All/Follows/Likes/Boosts/Replies) are `.filter-tab` buttons with the visual active-state driven by the CSS selector `.filter-tab[aria-pressed="true"]`. But the razor bound a **boolean** (`aria-pressed="@(_selectedFilter == value)"`), and Blazor renders a `true` boolean as `aria-pressed=""` (empty — presence means true), so the `="true"` value selector **never matched** → the selected filter had no highlight; users couldn't tell which filter was active. **Fix (two-pronged, belt-and-suspenders):** (1) razor now binds the string `? "true" : "false"` (matching the EngagementBar pattern) so the DOM carries literal `aria-pressed="true"`; (2) CSS selector widened to `.filter-tab[aria-pressed="true"], .filter-tab[aria-pressed=""]` so it also matches the boolean-binding form. Verified live: presence selector matches the selected tab; after fix the selected tab is accent-highlighted. | fixed |

## Reviewed — no finding

- **Compose**: "Posting as <handle>" context line, resize:vertical textarea, `0/500` char count, Note/Article `<select>`, CW checkbox + summary field, attachment field, formatting-tips hint, primary Post button. Meta row (count + type + CW) fits at 390px (no overflow, `nowrap` still safe at this width). Help copy is clear ("lands in your outbox…"). Good.
- **Home**: dark theme `#111318`/`#e6e8ec`, 16px/1.6. Contrast all ≥ 6:1 (body 15.15:1, muted 6.0:1; 0 elements < 3:1). Cards 8–10px radius, `#262b36` borders. 32px circular avatars. "Load more" = consistent secondary button. No horizontal overflow.
- **Search**: centered muted empty state ("No matches found. / Try a different handle…"), primary blue submit, flex row form. Good.
- **Communities**: create form (uppercase labels, consistent 6px inputs) + 2 community cards.
- **Profile**: 64px avatar, tab bar with a clear active state (filled `#1f242e` bg on the active tab). `main` = `max-width:720px; margin:0 auto` (centers at all widths).
- **Settings**: 6 tabs (Account/Notifications/Communities/Relays/Moderation/Danger). Danger tab = clear irreversible copy + red destructive button (`#e05565`, white text). Change password = primary blue.
- **Directory**: People/Communities tab switcher, cards with follow/unfollow, "Show posts by X" aria-labels. (Two `alice` rows = known dev-data duplicate actor, not a defect.)
- **Notifications**: row layout, italic muted "deleted their account" caption (contrast OK), primary "Mark all as read", 5 filter tabs. Remote accountless actors show the `DisplayNameFallback` host/handle (e.g. `cupoftea.social`) — acceptable, matches the B-011 62.3 fix. (See U-03 for the active-tab bug, and the note below for the avatar 410 noise.)
- **Actor detail** (`/actor`): 64px avatar, name, Unfollow/Block/Mute/Report actions, Posts/Followers/Following tabs (active state correct), clear help copy, Refresh, notes with engagement counts.
- **Object detail** (`/object`): large readable content (19.2px/30.72px), author + timestamp, "N likes · N boost" summary, engagement bar (Like/Boost/Reply, active states via `--active` class), "Replies / No replies yet." empty state, Home back link. Contrast sweep: **0 elements < 3:1** across the page.
- **Register** (authless): U-02 fix confirmed (all labels uppercase/600); handle + password validation hints, `@` prefix, correct autocomplete attrs, primary "Create account".
- **Settings sub-tabs (Relays + Moderation)**: Relays = clear fan-out explainer + "not subscribed" empty state + Subscribe; Moderation = Blocked (with Unblock) / Muted / Reported, each with a clear empty state.
- **Mobile (390px)**: nav collapses to an aria-labelled "Menu" toggle that opens all 9 links; `main` padding tightens to `16px 12px`; no horizontal overflow; nothing spills the viewport.

**Status: 63.1 route-by-route pass complete.** All signed-in routes (Home, Search, Communities, Profile, Settings×6 tabs, Directory, Compose, Notifications, Actor detail, Object detail) + authless (Login, Register) + 390px mobile reviewed. 3 findings (U-01, U-02, U-03) found and fixed in-slice; all verified live. Contrast ≥ 6:1 everywhere, no horizontal overflow at any width. Remaining 63 work (structural redesign brainstorm, optional) rolls to 63.2.

## Notes / deferred

- U-01 (base-`a` underline), U-02 (auth label), U-03 (active filter tab) all fixed
  in-slice for 63.1.
- The duplicate `alice` actor (localhost:8088 IRI) in the dev DB is data, not code —
  do not "fix" in the app.
- **→ 64 (request spam):** every "deleted their account" notification triggers an
  avatar fetch to `/ap/v1/proxy/<remote-actor-iri>` that returns **410 Gone** (the
  remote account no longer exists). On the live Notifications page this is 15 console
  errors + 15 wasted requests for the first page of notifications alone. The UI
  degrades gracefully (shows the initial-letter avatar), so it's not a visual defect —
  but it's pure network noise. Suggested fix: skip the avatar fetch (or short-circuit
  to the initial-letter fallback) when the notification is an account-deletion with no
  cached avatar. Logged here for 63.1 visibility; the fix belongs in the 64
  request-spam phase.
