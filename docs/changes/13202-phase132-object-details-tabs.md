# 132.2 — Object details view: Replies / Likes / Shares tab bar

**Date:** 2026-09-13
**Slice:** 132.2 (PLAN "Up Next" — surface the object's interactions in the object-detail view as a
tab bar, mirroring the actor-detail view's tab bar)
**Commits:** `feat(web): Phase 132.2 — object details tab bar (Replies/Likes/Shares)`

## Problem

The object-detail page (`/object?iri=…`, `ObjectDetail.razor`) rendered the object's main card and a
single flat "Replies" section. The object's **Likes** and **Shares** (boosts) were invisible in the
detail view — even though Phase 132.1 made the object document carry `iris:likedCount` /
`iris:sharedCount` / `iris:repliedCount`, and the server already serves the per-object `/likes` and
`/shares` collections (walking the like / announce reverse indexes). A reader opening a post could see
the engagement *counts* on the card's `EngagementBar` but had no way to see *who* liked or boosted the
post, and the replies were not grouped under a discoverable section.

## Change

`ObjectDetail.razor` now renders a **tab bar** (Replies / Likes / Shares) between the main object card
and the interaction sections, mirroring the actor-detail view (`ActorDetail.razor`):

- **Replies tab** (default active): the existing flat replies list, moved under the tab panel. The
  existing infinite-scroll "Show more replies" control and the "No replies yet." empty state are
  preserved.
- **Likes tab**: a new `InteractionActorsPanel` walks the object's `/likes` collection (the per-object
  likers surface), resolves each `Like` activity's actor, and renders an `ActorCard` grid.
- **Shares tab**: the same panel against the object's `/shares` collection (the per-object boosters),
  resolving each `Announce` activity's actor.

Each tab shows its count in the tab label, read from the object document's `iris:*Count` extensions
(Phase 132.1): `Replies (N)`, `Likes (N)`, `Shares (N)`. A count of `0` renders the label with no
parenthetical (e.g. just `Likes`), matching the actor-detail tab convention.

### New `InteractionActorsPanel` component

`apps/Iris.Web.Client/Components/InteractionActorsPanel.razor` is a focused variant of
`ActorListPanel`: it walks a Like/Announce **collection** (not an actor's following), extracts the
*actor* from each item, and renders actor cards.

- **Actor resolution (`ResolveActorIri`):** the server's `/likes` / `/shares` collections serve either
  the full `Like` / `Announce` activity (whose `actor` is the liker / booster) or a bare `Link` to the
  actor (an interaction recorded only as an edge). `ResolveActorIri` handles both shapes: the
  activity's actor when present, else the item's own IRI.
- **Pagination + hydration:** the panel loads a page via `IActivityPubClient.GetCollectionAsync`
  (signed session client), resolves each item's actor IRI, de-duplicates, then hydrates each actor
  document via `UiContext.GetActorAsync` and renders an `ActorCard`. A "Load more" sentinel + fallback
  button continue pagination. Loading, empty, and error states are handled.
- **Anonymous fallback:** when no signed client is available, the panel fetches the collection JSON
  anonymously and deserializes each item via `ActivityJson.Deserialize<IObjectOrLink>`.

### `ObjectDetail.razor` wiring

- `ActiveTab` state (default `"replies"`); `SwitchTab` flips it and re-renders.
- `ReplyCount` (`int?`) is read from the document's `iris:repliedCount` in `LoadEngagementAsync`
  alongside the existing `likedCount` / `sharedCount` reads.
- `LikesCollectionIri` / `SharesCollectionIri` are derived from the object's IRI
  (`iri.LikesOf()` / `iri.SharesOf()`) and passed to the panels.
- The tab bar uses the same ARIA `role="tablist"` / `role="tab"` / `role="tabpanel"` markup as the
  actor-detail view, so the existing `index.html` roving-tabindex + arrow-key script wires it up with
  no new JS.

## New / changed API

- `InteractionActorsPanel` (new Blazor component) — params `CollectionIri`, `Title`, `EmptyMessage`,
  `Client`, `AnonymousHttpClient`; renders a paginated, hydrated `ActorCard` grid of the actors
  behind a Like/Announce collection.
- `ObjectDetail.razor`: `ActiveTab`, `ReplyCount`, `LikesCollectionIri`, `SharesCollectionIri`,
  `SwitchTab(string)` added; `LoadEngagementAsync` now also reads `iris:repliedCount`.

## Verification

Live verification via MCP Playwright (per the web test policy — no new coded web tests; the live Docker
app, real browser, cache-bypass context to defeat the Blazor WASM immutable framework cache):

- **Tab bar renders** on a local post with interactions: `Replies (2) | Likes | Shares (1)` — the
  reply count (2), the absent like count (0 → bare `Likes`), and the boost count (1) are all correct,
  read from the object document's `iris:*Count`.
- **Replies tab** (default): shows the 2 replies in the flat list with the existing markup preserved.
- **Likes tab** on a *liked* post: `Likes (1)` label, and the panel resolves the liker and renders an
  `ActorCard` (the liker's handle + name).
- **Shares tab**: the panel resolves the booster (from the `Announce`) and renders its `ActorCard`.
- **Empty state**: a post with no likes shows `No likes yet.` in the Likes tab.
- **Tab switching**: clicking between tabs swaps the panels; returning to Replies preserves the list.
- **No console errors** across the loads.

`dotnet build Iris.slnx` — clean. `dotnet test Iris.slnx` — all tests pass (one known flaky
federation test passed on re-run under full-suite concurrency).

## Out of scope

- **Nested / expandable reply threading.** The Replies tab shows the existing *flat* replies list.
  Rendering the full threaded reply tree (replies-to-replies, collapsed by default) is a larger
  follow-up (the reply-collection data model supports it, but the UI work — thread construction,
  collapse/expand, per-reply nesting depth — is non-trivial and is tracked as a separate slice).
- **Cross-instance likers/boosters that are not yet stored.** The Likes / Shares panels walk the
  per-object `/likes` / `/shares` collections, which serve the like / announce edges this instance has
  recorded (locally authored, proxied-and-synced per 132.1, or from inbound activities). A liker on a
  remote instance whose Like activity was never seen by this instance is not listed (there is no edge
  to serve) — consistent with the 132.1 "serve what we know" fallback.
