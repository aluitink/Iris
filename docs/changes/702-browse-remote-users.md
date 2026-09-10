# 70.2 — browse remote users + view their content

## Outcome

Verified end-to-end (no code change required — the path was already correct from
Phase 20 federation + Phase 61 client work + Phase 64.1 actor-fetch coalescing):

- **Browse a remote actor** (`/actor?iri=https://mastodon.world/users/RayvenMX`):
  the page renders the remote profile (handle, display name, avatar, bio, follower /
  following counts) plus the actor's outbox — their posts, each with author handle,
  link/mention/hashtag parsing, and rich media attachments (a `.jpg` document rendered
  as a decoded `<img>`). **0 console errors.**
- **View a remote post** (`/object?iri=https://mastodon.world/users/RayvenMX/statuses/…`):
  the object detail renders the full remote Note — text, author, created timestamp, and
  media — **0 console errors.**
- **Actor-document fetch is coalesced to 1×** (`POST /ap/v1/proxy/…/users/RayvenMX`):
  Phase 64.1's in-flight coalescing gate in `UiContext.GetActorAsync` holds for remote
  actors too, so the profile + per-post `ActorAvatar`s all resolve from one cached
  fetch (verified on the network: a single proxy round-trip for the actor doc).

## Residual: per-post engagement walk still fires 2× for remote objects

While verifying, a residual request-spam issue was confirmed (not fixed this pass):

On the actor-detail page, each remote post's `EngagementBar` issues its per-object
`/likes` + `/shares` proxy walk **twice** (14 posts → 28 likes + 28 shares = 56 proxy
round-trips instead of 28). Root cause: `ObjectView` renders `ActorBar` → `ActorAvatar`,
whose `OnInitializedAsync` awaits `Ui.GetActorAsync(iri)`. When that fetch completes, the
implicit `StateHasChanged` re-renders the page, which **recreates** the `EngagementBar`
as a fresh component instance — so a per-instance `_countsLoaded` idempotency guard in
`EngagementBar.OnParametersSetAsync` (the natural 64.1-style fix) cannot catch it: the
second walk runs on a *different* instance whose `_countsLoaded` is still false.

A first attempt at the per-instance guard was made, verified live (the walk still fired
2×), and **reverted** (it is a no-op for this cross-instance re-render and would be a
misleading dead branch). The robust fix is a **shared per-object engagement-count cache**
on `UiContext` (mirroring the 64.1 actor-coalescing gate): key by object IRI, so a
re-created `EngagementBar` for the same post reuses the already-loaded counts instead of
re-walking. That is a multi-file change with regression risk to local-feed engagement
state (like/boost optimistically mutate the per-instance count; a shared cache must not
stale them), so it is deferred rather than landed speculatively.

**Tracking:** logged as a residual follow-up under Phase 64 (local N+1 actor-fetch
coalescing) — the same "async fetch → parent re-render → child re-walk" pattern, now for
the engagement walk. Not on the current Phase 70 critical path: 70.2's goal (browse
remote users + view their content) is met, and the 2× walk is a bandwidth/latency
efficiency issue, not a correctness or render one (all 56 requests return 200 and the
page renders correctly).

## Files

- No source change landed (the ineffective `EngagementBar` guard was reverted; the stray
  `samples/SampleBlazorClient/packages.lock.json` SDK-drift was reverted). Working tree is
  clean.
- Verification: live against a fresh `:8155` server (WASM republished, single
  `Iris.Web.Client.d8nxgntd79.wasm`) as `andrew` (cookie auth), browsing
  `mastodon.world/users/RayvenMX` (10 followers / 19 following, 14+ visible posts).
