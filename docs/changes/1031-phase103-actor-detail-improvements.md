# Phase 103 — Actor Detail Improvements

## What was built

Improved the actor detail page (`/actor?iri=...`) with four changes:

1. **Hydrated actor cards in Followers/Following tabs** — The Followers and Following tabs previously rendered a plain list of actor IRI links. They now render `ActorCard` components with the actor's avatar (icon or fallback initial) and name, hydrated via `Ui.GetActorAsync`. A new `ActorListPanel` component handles fetching the paged collection, hydrating each actor document, and rendering the card grid with infinite scroll.

2. **Clickable avatar in actor profile header** — The avatar in the actor detail header (and the profile page header) is now wrapped in a link to the actor's own detail page, so clicking it navigates to the full actor view.

3. **Removed "This is you." text** — The actor detail page no longer shows "This is you." for the signed-in actor's own profile. The header simply shows the avatar, name, and (when authenticated) the Follow/Unfollow + moderation buttons.

4. **Header layout fix** — The Follow/Unfollow and moderation buttons are now wrapped in an `.actor-detail-actions` div that positions them in the right column of the header, preventing them from wrapping awkwardly when the actor description is long.

## Key types

- **`ActorListPanel.razor`** (new) — A reusable component that:
  - Accepts a collection IRI (followers/following) and an optional `IActivityPubClient` (for authenticated hydration) or falls back to `HttpClient` (anonymous).
  - Fetches the paged collection via `PagedCollection`.
  - Hydrates each actor entry via `Ui.GetActorAsync`, replacing the bare IRI with the full `IActor` document.
  - Renders an `ActorCard` grid with infinite scroll (reuses the Phase 98 sentinel pattern).
  - Handles both authenticated and anonymous access paths.

- **`ActorDetail.razor`** — Updated to use `ActorListPanel` for the Followers and Following tabs (replacing the old plain-list rendering). The "This is you." text was removed. The Follow/Moderation buttons are wrapped in `.actor-detail-actions`.

- **`ActorProfile.razor`** — The avatar is now wrapped in `<a href="@ActorHref" class="actor-profile-avatar-link">`.

## CSS changes

- `.actor-list-panel` — container for the hydrated actor card grid.
- `.actor-list-grid` — CSS grid layout for actor cards (responsive: 1 column on mobile, 2+ on desktop).
- `.actor-detail-actions` — flexbox wrapper for the Follow/Moderation buttons in the actor header.
- `.actor-profile-avatar-link` — hover styles for the clickable avatar (slight scale + opacity change).

## Test counts

- **0 new coded tests** (WASM manual-test policy — Phase 45+).
- Full fast suite: **1415 passed / 0 failed** (excluding known flaky federation tests that pass in isolation).
- Live Playwright-verified:
  - Followers tab: shows alice (avatar "A" + name + correct link) and andrew (name "Andrew Luitink" — hydrated from actor document).
  - Following tab: shows alice with correct link.
  - Avatar in header is a clickable link.
  - No "This is you." text on own profile.
  - Header layout correct (buttons in right column).
  - 0 console errors.

## Decisions

- **`ActorListPanel` as a separate component** — Keeps the hydration logic (fetch → hydrate → render) isolated from the `ActorDetail` page, making it reusable for the Profile page's Followers/Following tabs (Phase 106) without duplication.
- **Anonymous hydration via `HttpClient`** — When signed out, `Ui.GetActorAsync` uses a plain `HttpClient` (unsigned) to fetch actor documents. Remote actors will fail due to CORS (expected); local actors resolve via the same-origin media proxy.
- **`Iri` nullability handling** — The paged collection returns `IriValue` strings; the component converts them to `Iri` at the boundary before passing to `Ui.GetActorAsync`.
