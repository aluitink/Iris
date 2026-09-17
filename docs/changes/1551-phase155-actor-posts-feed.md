# Phase 155 — Actor Posts Feed

**Status:** COMPLETE (live-verified, build clean 0/0, web tests 106/106, 0 console errors/warnings)
**Date:** 2026-09-17

## What

The actor detail page's Posts (outbox) cards previously rendered the per-card **Block / Mute / Report**
moderation buttons. That was redundant: the actor page already offers those same moderation actions
**once, at the actor level, in the page header**. This phase removes the per-card moderation buttons from
the actor's outbox cards so they use the **common card controls** — looking the same as the main feed and
the profile outbox feed — while keeping the actor-level moderation controls in the header.

Every other card control is untouched: the actor bar, the content, the whole-card link overlay (Phase 153),
and the Like / Boost / Reply engagement bar all still render on the outbox cards. Only the redundant
per-post moderation buttons are gone.

## How

Client-only. Three small changes:

### `apps/Iris.Web.Client/Components/ObjectView.razor.cs`

- New `[Parameter]` **`ShowModeration`** (`bool`, default `true`). When `false`, the card suppresses the
  per-card moderation buttons. The default `true` keeps the main feed, the object detail page, the
  notifications page, and every other `ObjectView` consumer rendering their moderation controls exactly as
  before.

### `apps/Iris.Web.Client/Components/ObjectView.razor`

- The two `CardModerationButtons` render blocks (the `Create` branch and the direct-object branch) are
  each gated on `ShowModeration &&` in addition to the existing `CanModerateAuthor(...)` check:
  - `@if (ShowModeration && CanModerateAuthor(ActivityActorIri) && ActivityActorIri is { } modAuthor)`
  - `@if (ShowModeration && CanModerateAuthor(AuthorIri) && AuthorIri is { } modAuthor)`

### `apps/Iris.Web.Client/Components/Pages/ActorDetail.razor`

- The Posts tab's `PagedCollection` now passes an explicit `ItemTemplate="OutboxItemTemplate"` (previously
  it used the default `<ObjectView Item="item" />`).
- New `OutboxItemTemplate` (`RenderFragment<IObjectOrLink>`) renders the common `ObjectView` card with
  `ShowModeration="false"` — mirroring the existing `Profile.PostTemplate` pattern (which renders the
  profile outbox cards). This is the only consumer that opts out of per-card moderation.

## Decision

- **Opt-out via a parameter (default on), not a separate card variant.** `ObjectView` is the single shared
  card used by every feed; adding a `ShowModeration` flag (defaulting to `true`) is the minimal change that
  lets exactly one context (the actor outbox) suppress the per-card buttons without touching any other feed.
  The actor-level moderation in the page header is the single source of truth for moderating that actor, so
  the per-post buttons are pure duplication there.

## Verification

- **Build:** `dotnet build -c Release` (full solution) — 0 warnings / 0 errors.
- **Tests:** `dotnet test tests/Iris.Web.Tests -c Release --no-build` — 106/106 passed, 0 failures.
  (No new coded tests per the WASM manual-test policy — Phase 45+ verification is live Playwright.)
- **Live (docker, `irisweb-iris-web-1`, `irisweb-db-1`):**
  - **Build gotcha (recurring):** `docker compose build --no-cache iris-web` used to force a recompile; a
    **fresh browser context** (close + reopen) is required to bust the browser's HTTP cache of the
    content-hashed WASM. Verified the new code is in the deployed wasm via a UTF-16LE string search for
    `ShowModeration`.
  - **Actor page outbox (alice's page):** 12 outbox post cards render with **0** per-card
    `.card-moderate-btn` elements (before this change, each of the 12 cards carried the 3-button
    Block/Mute/Report set). The **actor-level** moderation controls (`.moderation-actions` Block / Mute /
    Report) are still present in the page header — moderation is offered once, at the actor level.
  - **Common card controls retained:** all 12 outbox cards still render the `.engagement-bar` (Like/Boost/
    Reply), the actor bar, the content, and the Phase-153 whole-card link overlay
    (`.object-card-link` + `object-item--clickable`) — so they look identical to the main feed / profile
    outbox cards. Only the redundant per-post moderation buttons are gone. Screenshot-confirmed.
  - **No regression to other feeds:** the `ShowModeration` gate defaults to `true`, so the main feed
    (`/home`) and the object detail page render their cards identically to before (the git diff shows the
    only functional change is the added `ShowModeration &&` gate, which is `true` by default for every
    consumer that doesn't set it).
  - **0 console errors, 0 warnings** across the whole session (login → alice's actor page → home feed).

## Files

- `apps/Iris.Web.Client/Components/ObjectView.razor.cs`
- `apps/Iris.Web.Client/Components/ObjectView.razor`
- `apps/Iris.Web.Client/Components/Pages/ActorDetail.razor`
- `PLAN.md`, `docs/ROADMAP.md`, `docs/plans/production-app-feature-matrix.md`
