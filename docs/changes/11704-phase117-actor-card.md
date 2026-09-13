# 117.4 — Common Actor Card

## Summary

Upgraded `ActorCard` from a minimal avatar + text row into a polished, reusable actor
card with banner support, type badges, and inline moderation actions. The card is now
the canonical actor presentation component across the directory, followers/following
lists, and search results.

## Changes

### ActorCard.razor

- **Banner support**: When the actor document carries an `image` (the AS `image`
  property — Mastodon's banner), the card renders a banner strip on top with the
  avatar overlapping its bottom edge (Mastodon-style). The card switches to a
  column layout (`.actor-card--bannered`).
- **Identity row**: Handle, display name, and type badges are now grouped in a
  single `.actor-card-identity` row with baseline alignment.
- **Type badges**: A `Group` actor shows a "Community" badge; an `Application`
  actor shows a "Bot" badge.
- **Summary clamp**: The bio is clamped to 2 lines (`-webkit-line-clamp: 2`) to
  keep cards compact in lists.
- **Actions column**: A new `.actor-card-actions` container on the right holds
  `ChildContent` (e.g. a FollowButton) plus an optional `ModerationActions`
  component. Controlled by the new `ShowModeration` parameter (default `true`).
- **`ShowModeration` parameter**: When `false` (used by `DirectoryCard`, which
  provides its own follow button), the moderation menu is omitted.
- **Correct href**: The handle link now uses `ActorIdentityHelper.ActorHref(iri,
  actor)` which routes `Group` actors to `/community?iri=…` instead of
  `/actor?iri=…`.

### DirectoryCard.razor

- Passes `ShowModeration="false"` to `ActorCard` (the directory row provides its
  own `FollowButton` + chevron; moderation is available on the actor detail page).

### CSS (app.css)

- `.actor-card`: added `flex-wrap: wrap` for the bannered column layout.
- `.actor-card--bannered`: column layout, banner strip (4.5rem), avatar overlap
  with negative margin + panel-colored border.
- `.actor-card-main`: new wrapper for avatar + body row (flex: 1 1 auto).
- `.actor-card-identity`: baseline-aligned flex row for handle + name + badges.
- `.actor-card-badge`: small pill badge; `--community` (accent-tinted), `--app`
  (neutral inset).
- `.actor-card-summary`: 2-line clamp.
- `.actor-card-actions`: right-aligned action column (flex-shrink: 0,
  margin-left: auto).
- `.actor-list-grid .actor-card`: added `overflow: hidden` for bannered cards;
  `.actor-card--bannered` overrides padding to 0 with inner padding on
  `.actor-card-main`.
- `.directory-card-expand-btn .actor-card`: `flex-wrap: nowrap` to keep the
  directory row single-line; `.actor-card-actions` hidden; summary clamped to 1
  line.

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — all 1,141 tests pass (1 flaky federation test passed in isolation).
- Live verification (Playwright on Docker app):
  - **Directory**: Andrew's card shows banner + avatar overlap + name; alice/bob
    show clean cards without banners.
  - **Actor detail (Followers tab)**: RayvenMX (remote) shows banner + moderation
    buttons (Block/Mute/Report).
  - **Actor detail (Following tab)**: Mixed list — alice (no banner), RayvenMX
    (banner + moderation), test-community-541 (Community badge + summary +
    moderation).
  - **Search**: Community result shows handle + name + summary correctly.
  - No console errors (only expected 502s from the media proxy for unreachable
    remote hosts).
