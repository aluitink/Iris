# 133.1 — Object card styling: two-row header (date on top, handle + moderation below)

**Date:** 2026-09-13
**Slice:** 133.1 (PLAN "Up Next" — calm down the object card header on mobile)
**Commits:** `d36473b` — `feat(web): two-row object card header — date on top, handle+moderation below (Phase 133.1)`

## Problem

The object card header (the top of every post card) packed three things into a single
flex row: the actor's avatar + handle (left), the moderation bar (block/mute/report,
middle), and the relative post date (pushed to the far right with `margin-left: auto`).
On a narrow (mobile) screen this is too busy, and a **long username** crowds the
timestamp — the handle and the date fight for the same horizontal band, and the card
can overflow. The goal: give the date its own line and keep the header calm.

## Change

The card header is restructured from one row into **two left-justified rows**:

- **Row 1 — the date.** The relative time (`<time class="object-time">`) is wrapped in a
  full-width `.object-header-time` div and sits on its own top line, left-justified. It no
  longer has `margin-left: auto`, so it is no longer pushed to the far right.
- **Row 2 — the actor + moderation.** The `ActorBar` (avatar + handle) and the
  `CardModerationButtons` (block/mute/report) are wrapped together in a full-width
  `.object-header-actor` div, directly under the date. The moderation group sits
  **left-justified, directly after the handle** (a small `gap`), rather than being pushed
  toward the right edge.

`.object-header` itself becomes a vertical flex column (`flex-direction: column;
align-items: flex-start`) with a small row gap, so the two rows stack and both align to
the card's left edge.

Applied to the two card headers that carry an `ActorBar` + moderation bar:

- the **primary `Create`/Note card** (`ObjectView.razor`, the `Item is Create` branch) —
  this is the card rendered in the home timeline, the actor profile's Notes/Replies tabs,
  and search; it is the one users actually see;
- the **generic `IObject` fallback card** (`ObjectView.razor`, the `Item is IObject { Id: … }`
  branch) — kept consistent with the primary card.

The `Like` / `Follow` / boosted-object headers were left as-is: they use a different,
lighter structure (an action label + a plain handle link, no avatar or moderation bar), so
the "long username crowds the date" problem does not apply to them in the same way.

### CSS

New / changed rules in **both** `app.css` copies (kept in sync per the project convention):

- `.object-header` — `display: flex; flex-direction: column; align-items: flex-start;
  gap: var(--space-15)`.
- `.object-header-time` (new) — `width: 100%` (row 1, the date line).
- `.object-header-actor` (new) — `display: flex; align-items: center; gap: var(--space-2);
  width: 100%; min-width: 0` (row 2, the handle + moderation line; `min-width: 0` lets a
  long handle truncate/wrap instead of forcing the card wider).
- `.object-time` — dropped `margin-left: auto` so the date is left-justified on its own line.

## New / changed API

None — markup + CSS only. No C# or API surface changed.

## Verification

Live verification via MCP Playwright (per the web test policy — no new coded web tests;
live Docker app, real browser, **fresh context + `Network.setCacheDisabled` /
`Network.clearBrowserCache` via CDP** to defeat the Blazor WASM immutable framework
cache). The container was rebuilt `--no-cache` and the served
`Iris.Web.Client.*.wasm` was confirmed (over HTTP, byte-level) to contain the new markup
before browser verification.

- **Two rows render:** one post card's header DOM is now
  `<div class="object-header"><div class="object-header-time"><time …>…</time></div>
  <div class="object-header-actor"><span class="actor-bar">…handle…</span>…</div></div>`.
- **Date on top, left-justified:** the `<time>`'s top edge is above the actor bar's, and
  both rows share the same left x (the card's content edge) — measured `time-on-top` and
  `bothLeftJustified: yes` on a 1280 px viewport.
- **Moderation left-justified after the handle:** on a card where the moderation bar is
  shown (an admin viewing another actor's post), `.card-moderate` is *inside*
  `.object-header-actor`, its left edge sits just right of the handle's right edge
  (`modRightAfterActor: true`), and it is on the same row (`sameRow: true`) — no longer
  pushed to the far right.
- **Mobile (375 px):** no horizontal overflow (`scrollWidth == innerWidth`), the two rows
  still stack with the date on top, and the handle fits inside the card (`actorFitsInCard`).
- **No console errors** on the home timeline load.

`dotnet build Iris.slnx` — clean. `dotnet test Iris.slnx` — all tests pass (one known-flaky
federation test in `Iris.Server.Tests` passed on isolated re-run; this slice is client-only
and touches no server code).

## Out of scope

- **The `Like` / `Follow` / boosted-object card headers** — they keep their existing single
  row (action label + handle link). They could be given the same two-row treatment later if
  long handles become a problem there, but that is not this slice.
- **Truncation policy for extremely long handles** — `.object-header-actor` has
  `min-width: 0` so the row can shrink, but no explicit ellipsis rule was added; the
  existing `.actor-bar-handle` behavior applies.
