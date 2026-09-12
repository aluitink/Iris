# Phase 107 — Search Improvements: Group → Community Routing

## What

When a user searches for a Lemmyverse community (e.g. `!technology@lemmy.world`), the
result was a Group actor that linked to the generic `/actor?iri=…` page. Now Group
actors link to the specialized `/community?iri=…` page (which has Feed, Members, Join,
Follow, and Post-to-community actions) and display a `c/` prefix (Lemmy-style).

## Key Changes

- **`ActorIdentityHelper`** — added `IsCommunity(IObject?)` (checks `actor is Group`)
  and `ActorHref(Iri, IObject?)` overload that returns `/community?iri=…` for Group
  actors, `/actor?iri=…` otherwise.
- **`ActorProfile`** — avatar and handle links now use the Group-aware `ActorHref`.
- **`ObjectView`** — actor search results use the Group-aware `ActorHref`; Group actors
  get the `object-actor--community` CSS class.
- **`NotificationRow`** — `ActorHref` now delegates to `ActorIdentityHelper.ActorHref`.
- **CSS** — `.object-actor--community .object-actor-handle::before` adds a muted `c/`
  prefix (e.g. `c/technology`).

## Verification

- `dotnet build` clean (0 warnings, 0 errors).
- `dotnet test` green (1419 passed / 0 failed; 1 known flaky federation test passes in isolation).
- Live Playwright-verified (fresh browser context, cache disabled):
  - Search `!technology@lemmy.world` → result shows `c/technology` with `c/` prefix.
  - Link href is `/community?iri=https%3A%2F%2Flemmy.world%2Fc%2Ftechnology`.
  - Clicking the link (or navigating directly) renders the full community page:
    header with banner, Follow/Join buttons, Feed + Members tabs, "Post to this community".
  - Feed shows "No posts in this community yet" (expected — Lemmy's Group actors
    don't expose an ActivityPub `outbox` at `/feed`; they use their own API).
  - 0 unexpected console errors (3 expected 404s on `/feed` and `/members` sub-paths).
- 0 new coded web tests (WASM manual-test policy).

## Decision

The `c/` prefix is rendered via CSS `::before` pseudo-element (not in the Razor
template) so the accessible text remains the community name alone, while the visual
prefix is decorative. This matches Lemmy's own rendering approach.
