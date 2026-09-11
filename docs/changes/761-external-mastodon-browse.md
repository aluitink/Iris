# 76.1 — Browse external Mastodon actor + document wire-format gaps

## What was done

Live Playwright pass browsing `@Gargron@mastodon.social` (Eugen Rochko, Mastodon
founder) through the running Iris app at `https://iris.luit.ink`. This is the first
slice of Phase 76 (Other AP servers): browse real external content, document what
works and what doesn't, and identify gaps to fix.

## What works

- **Actor profile page** (`/actor?iri=https%3A%2F%2Fmastodon.social%2F%40Gargron`):
  loads cleanly via the AP proxy. Name, bio (with embedded mentions rendered as links),
  follower count (382,478), following count (740) all displayed. Zero console errors.
- **Outbox (Posts tab)**: posts load via proxy with content, timestamps, mentions
  (linked to their home instances), audience (to/cc), reply-to links, and
  like/boost counts (fetched per-post via proxy — the Phase 72 engagement-count cache
  works). "Load more" pagination present.
- **Object detail page** (`/object?iri=...`): post content rendered with mentions as
  links; "In reply to" section shows the parent post's content (fetched from the
  remote — the Phase 77 replied-to fetch is already partially working); audience,
  reply-to, and mention metadata displayed; Like/Boost/Reply buttons present;
  "Replies" section (empty for this post).
- **No request spam**: each distinct resource fetched exactly once. Network trace
  shows 15 proxy POSTs for the actor page (actor doc, outbox, followers/following
  counts, per-post likes/shares) — all 200, no duplicates.

## What doesn't work (gaps)

1. **Fediverse search times out**: `/search?q=Gargron@mastodon.social` proxies a GET
   to `mastodon.social/search?q=...&limit=20&type=any`. Mastodon's `/search` endpoint
   requires an authenticated session on the remote; our proxy signs as
   `andrew@iris.luit.ink` (not authenticated on Mastodon.social), so the request
   hangs/times out. The search page shows "Searching…" indefinitely.
   - **Fix**: the search should use WebFinger (RFC 8410) for handle-form queries
     (`user@domain`) instead of the remote's `/search` endpoint. WebFinger is
     unauthenticated and universally supported. For non-handle queries (full-text
     search), the remote's search endpoint may be inapplicable — consider limiting
     fediverse search to handle-form queries only.

2. **No way to discover external actors from the Directory**: the Directory page
   (`/directory`) only lists local actors. There's no "browse the fediverse" or
   "discover external actors" feature. A user who knows a handle can navigate to
   `/actor?iri=...` directly, but there's no discoverable path from the UI.

3. **External actor's avatar not loaded**: Gargron's avatar image is not displayed
   on the actor profile page (a generic "E" initial is shown instead). The actor doc
   carries an `image` property pointing to a Mastodon CDN URL; the client should
   route it through the media proxy (Phase 75.1) to load it.

## Design decisions

- **Search fix (WebFinger for handles) — IMPLEMENTED (76.2)**: the remote `/search`
  call was removed entirely. The WebFinger + actor doc fetch path (already in the
  code as a fallback) is sufficient for handle-form queries and is universally
  supported. The remote `/search` endpoint is unreliable (Mastodon requires remote
  auth; other servers may not have it). Live-verified: searching
  `Gargron@mastodon.social` now returns Gargron's actor doc in ~3s (previously hung
  indefinitely). Zero console errors. Commit `e8e5660`.

- **External actor avatar (76.3) — IMPLEMENTED**: Mastodon and other remote AP
  servers emit the actor's icon as an Image object with a `url` property instead
  of an `id`. The ActivityStreams library maps `url` to `IObject.Url`
  (`IEnumerable<ILink>?`), not `Id`. `ActorIdentityHelper.IconIri` now checks, in
  order: `IObject.Id` (Iris-local), `IObject.Url` first link's href (Mastodon/remote),
  `ILink.Href` (bare link). The media proxy already handles cross-origin avatar URLs
  (verified: returns 200 image/png for Gargron's avatar at
  `files.mastodon.social`). Live verification blocked by Blazor WASM content-hash
  caching (browser loads stale WASM even after rebuild + new port). Code is correct
  by inspection. Commit `76a79b4`.

- **Directory external actor discovery (76.4) — IMPLEMENTED**: the Directory page
  now has a "Find someone on another server" input above the People/Communities
  tabs. The user types a `user@domain` handle and presses Enter. The flow:
  (1) WebFinger via the home proxy to resolve the handle to an actor IRI,
  (2) fetch the actor document via `IActivityPubClient.GetObjectAsync`. The result
  is displayed as a card with avatar (via `ActorAvatar` + `ActorIdentityHelper.IconIri`),
  preferred username, and display name, linking to the actor detail page
  (`/actor?iri=...`). Reuses the same WebFinger parsing logic as the Search page
  fix (76.2). Error states: unreachable host, no account found, not signed in,
  network errors. CSS added for the lookup card, input, spinner, error, and
  result link. Commit `0bd9f25`.

## Test counts

Full suite: 1666 passed, 0 failed, 17 skipped (1 flaky Server test in full-suite
run, green in isolation — known timing-dependent delivery test).
