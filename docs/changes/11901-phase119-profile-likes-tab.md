# 119.1 — Profile improvements: render the liked object (not just a link) in the Likes tab

## Summary

The profile **Likes** tab rendered every `Like` as a bare IRI link (e.g.
`https://iris.luit.ink/ap/v1/u/alice/notes/…`) under a "Liked / andrew" header. The user could not
see *what* was liked — only the URL. This phase resolves the liked target so each entry shows the
actual post (author, content, media, engagement) with a clear "Liked" indication, instead of a dead
link.

## Context

A `Like` activity published to the actor's own outbox carries its target as a **bare link** — the
server's `ActivityPubClient.LikeAsync` sets `Object = [new Link { Href = objectId }]`, with no
embedded object. In `ObjectView`, the `Like` branch already rendered a "Liked" header and then fell
back to:

- `LikedContent` (the embedded target's content) — **empty**, because there is no embedded object; or
- a bare `<span class="object-iri">` IRI link.

So every Likes-tab entry was a link. The `ObjectView` component already had the machinery to fetch a
missing related object (the 90.1 "in reply to" parent fetch in `OnInitializedAsync`), so the fix
reuses that pattern for the `Like` target.

## Changes

### ObjectView.razor.cs

- **`_likedObject`** (new field): the resolved liked object, `null` until fetched.
- **`OnInitializedAsync`** — a new block after the parent-fetch: when the item is a `Like` whose
  target is a bare link (`ActivityEmbeddedObject is null` and `LikeTargetIri` is present), fetch the
  liked object via `client.GetObjectAsync(likedIri)` and store it in `_likedObject`.
  - **`await Session.EnsureReadyAsync()`** is called first. `IActorSessionAccessor` is registered
    **scoped**, and its `Client` getter returns `null` until the signing key has loaded (the getter
    doc says so explicitly). The parent *page* (Profile) calls `EnsureReadyAsync`, but this component's
    own scoped instance may not have its key loaded at the moment `OnInitializedAsync` runs — without
    priming it here, `Session.Client` is `null` and the fetch is silently skipped (the original bug:
    the tab stayed bare links).
  - Non-fatal on failure (a failed fetch leaves `_likedObject` null → the card falls back to the bare
    link, exactly as before).

### ObjectView.razor

- The `Like` markup branch now checks `_likedObject` **first**: when resolved, it renders the liked
  object as a full nested `<ObjectView Item="liked" />` inside `<div class="object-like-resolved">`
  (author, content, media, like/boost counts) under the "Liked" header. Otherwise it keeps the
  existing `LikedContent` / bare-IRI-link fallback.

### app.css (both `wwwroot/css/app.css` copies — kept in sync)

- **`.object-like-resolved`**: `margin-top`, `padding-left`, and a 2px left accent border
  (`--accent-warm`) so the resolved card reads as the liked content nested under the "Liked" header.

## Verification

- `dotnet build Iris.slnx` — 0 warnings / 0 errors. `dotnet test Iris.slnx` — all suites green
  (the only intermittent failure is the known load-flaky
  `FollowEdgeConvergenceIntegrationTests`, which passes in isolation).
- Live (Playwright, fresh browser with caching disabled, signed in as `andrew`):
  - The Likes tab renders **10/10** liked objects as full nested cards — **0** bare-IRI-link
    fallbacks.
  - Resolved entries show real content (e.g. "lowqualityfacts … History is so interesting.",
    "RayvenMX … hello😍") with author, timestamp, audience, and like/boost counts.
  - Both **local** (`iris.luit.ink/ap/v1/u/alice/notes/…`) and **remote**
    (`mstdn.social`, `mastodon.world`) liked objects resolve — remote fetches succeed through the
    same-origin proxy.
  - No console errors. The `.object-like-resolved` accent styling is present in the deployed CSS.

## Open / follow-ups

- The resolved fetch is one extra `GetObjectAsync` per visible Like card (10 per page). The client's
  actor cache dedupes repeat reads of the same target, so re-rendering a page does not re-fetch. A
  future optimization could batch-resolve a page's targets in one call.
- The "Liked" indication is the existing "Liked" header (pre-119.1) plus the now-resolved content. A
  dedicated "You liked this" badge is possible but out of scope here.
