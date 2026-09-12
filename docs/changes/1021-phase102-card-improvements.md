# Phase 102 — Card Improvements

## Summary

Two improvements to the post card (the `object-item` rendered by `ObjectView`):

1. **Larger card icon**: The card header avatar is enlarged from 2rem (32px) to 2.5rem (40px) so the
   post's identity reads more strongly. On small screens (≤768px) it steps back down to 2rem so the
   handle + timestamp keep room on the header line.
2. **Minimalistic moderation buttons on the right**: Content cards now show a compact, low-key
   **Block / Mute / Report** icon group on the **right side of the card header** (between the author
   and the timestamp). The buttons are subtle (55% opacity, revealed to full on hover/focus) so the
   header stays clean, but the actions are reachable without opening the "more" menu in the
   engagement bar. They are shown only when the signed-in viewer may moderate the author (a distinct
   actor) — they do **not** appear on the viewer's own posts.

## Changes

### UI (new `CardModerationButtons.razor` + `ObjectView.razor`/`.razor.cs`)

- **New `CardModerationButtons` component** (`apps/Iris.Web.Client/Components/CardModerationButtons.razor`):
  a `div.card-moderate` holding three icon-only `button.card-moderate-btn` (Block = circle-with-slash,
  Mute = speaker-off, Report = flag). Each button carries a `title`/`aria-label` naming the handle
  ("Block bob", etc.). It takes `Author` (the target IRI), `Busy` (disables all three while a
  moderation action is in flight), and three `EventCallback`s (`OnBlock`/`OnMute`/`OnReport`).
- **`ObjectView.razor`**: in both the `Create` (feed) branch and the direct-object (`IObject`)
  branch, a `<CardModerationButtons .../>` is rendered in the card header when
  `CanModerateAuthor(AuthorIri)` is true, between the `<ActorBar/>` and the `<time>`.
- **`ObjectView.razor.cs`**: added
  - `CanModerateAuthor(Iri? author)` — true when the viewer is signed in, the author is known, and
    the author is a distinct actor (you cannot moderate yourself from a card header).
  - `CardBlockAsync` / `CardMuteAsync` / `CardReportAsync` — fire-and-forget moderation actions.
    Block + Report go through the signed `IActivityPubClient` (`BlockAsync`/`FlagAsync`); Mute goes
    through the `ILocalModerationClient` (`MuteAsync`). All share a single `_cardModBusy` flag (one
    action at a time) and are non-fatal on failure (the button simply stays enabled for a retry).
    Each re-renders on completion via `StateHasChanged()`.

### CSS (`app.css`, both the client and the server copy)

- **Card avatar enlargement** (scoped so only feed cards grow):
  - `.object-header .actor-avatar` / `img` → `2.5rem` (was the generic 2rem from `.actor-avatar`).
    Scoped under `.object-header` so the actor-detail header, profile, directory, and notification
    row avatars are untouched.
  - `.object-header .actor-avatar-fallback` → `font-size: 1rem`.
- **Mobile step-down** (inside the existing `@media (max-width: 768px)` block):
  - `.object-item .object-header .actor-avatar` / `img` → `2rem`, `.actor-avatar-fallback` → `0.9rem`.
    The extra `.object-item` segment raises specificity above the **later-declared** base
    `.object-header .actor-avatar` 2.5rem rule (equal-specificity rules resolve by source order, so
    without the extra segment the desktop 2.5rem would have won even inside the media query).
- **`.card-moderate` group**: `display: inline-flex`, `gap: 0.1rem`, `margin-left: 0.25rem`,
  `opacity: 0.55` → `1` on `.object-header:hover` / `:hover` / `:focus-within`.
- **`.card-moderate-btn`**: `1.5rem` square icon button (no background/border), muted color, hover
  → accent + `--hover` background, `:disabled` dimmed. `svg` at `0.9rem`.
- **Mobile**: `.card-moderate` gap/spacing tightened.

## Tests

UI-only change (Blazor WASM). **0 new coded web tests** — per the WASM manual-test policy
(Phase 45+), verification is manual via MCP Playwright on the live Docker app.

Full suite (server + client, unchanged by this UI work): **1415 passed / 0 failed** (17 skipped).

## Verification

Live-verified via Playwright on the live Docker app (logged in as `alice`):

- **Desktop (1440px)**: all six card-header avatars measure **40px** (2.5rem).
- **Mobile (375px)**: all six card-header avatars measure **32px** (2rem) — the media query applies.
- **Moderation buttons**: on bob's card (a different actor) the header shows `Block bob` /
  `Mute bob` / `Report bob` positioned **before the timestamp** (right side). On alice's own cards
  **no** moderation buttons render (you cannot moderate yourself).
- **Mute end-to-end**: clicking "Mute bob" fires
  `POST /local/v1/u/alice/mutes/https://iris.luit.ink/ap/v1/u/bob` → **204 No Content**. Un-muted via
  `?unmute=true` (204) to restore state.
- **0 console errors** after the full walk.

## Decisions

- **Avatar scoping**: targeted `.object-header .actor-avatar` rather than the generic `.actor-avatar`
  so the enlargement is confined to feed cards. The card header renders an `ActorBar`→`ActorAvatar`
  (class `actor-avatar`), not the (now-unused-in-cards) `.object-avatar` class.
- **Specificity for the mobile rule**: the base 2.5rem rule is declared *after* the `@media` block in
  source, so an equal-specificity `.object-header .actor-avatar` inside the media query loses. The
  mobile rule is prefixed with `.object-item` to win on specificity (not by reordering, which is
  fragile).
- **Separate component**: extracted `CardModerationButtons` rather than inlining the three buttons in
  both `ObjectView` branches — it keeps the two call sites DRY and isolates the icon SVGs + the
  handle-resolving tooltip logic.
- **Fire-and-forget, non-fatal**: header moderation is a quick-action surface; a failed delivery
  doesn't need a toast (the button stays enabled for a retry), matching the existing
  `EngagementBar` moderation handlers' contract.
- **Self-moderation hidden**: `CanModerateAuthor` requires the author to be a distinct actor, so the
  viewer's own posts never show the group (consistent with `EngagementBar`'s `CanModerate`).
