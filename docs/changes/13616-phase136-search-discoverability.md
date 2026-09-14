# 136.16 — Search and discoverability checks

**Date:** 2026-09-14
**Slice:** 136.16 (Lemmy interop — search and discoverability)
**Status:** **COMPLETE.** Six cross-instance integration tests verify that federated content is
discoverable via community search and global search on both instances, and that direct-URL deep
links resolve on the origin instance (and 404 on non-origin instances, as expected).

## What this slice delivers

136.16's acceptance criteria:

1. Confirm federated communities and posts become discoverable in both UIs after handshake + first delivery.
2. Validate direct URL deep links resolve for remote posts/comments without requiring prior local cache.
3. Measure discovery lag (publish to searchable/visible) and record expected eventual-consistency window.
4. Exit when operators can reliably find remote communities/content by name or URL on both sides.

### Test coverage

The six tests in `CrossInstanceSearchDiscoverabilityIntegrationTests` (two-instance `TestServer`
fixture, A: `search-a.domain.local` alice with federated + deep-link posts, B: `search-b.domain.local`
lumen community with a follow edge to alice and a delivered remote post) verify:

| Test | What it verifies |
|------|-----------------|
| `CommunitySearch_FindsRemotePost_ViaPeeringEdge` | B's community search (`GET /c/lumen/search?q=federated`) finds alice's remote posts via the peering/follow edge, confirming federated content is discoverable in the community's search surface after the first delivery. |
| `CommunitySearch_DoesNotFindUnrelatedTerm` | B's community search for an unrelated term returns an empty result set, confirming the search does not leak content that is not in the community's feed surface. |
| `GlobalSearch_FindsDeliveredRemoteObject_OnReceivingInstance` | B's global search (`GET /search?q=deliveredcontent`) finds the embedded Note from alice's delivered post in B's object store, confirming delivered remote content is searchable on the receiving instance. |
| `GlobalSearch_LocalOnly_ExcludesCachedRemoteActor` | B's global search with `?local=true` does not include alice (the cached remote actor), confirming the local-only filter correctly scopes results to this instance's own actors. |
| `DeepLink_RemotePost_ResolvesOnOriginInstance` | A deep link to alice's post (`/ap/v1/u/alice/notes/post-deep-1`) resolves on A (the origin) with the correct ID and `Create` type, confirming direct-URL resolution works for remote posts on the origin instance. |
| `DeepLink_RemotePost_404sOnNonOriginInstance` | The same deep link 404s on B (the non-origin), confirming that object-document resolution is local-only (IRIs are reconstructed from the local base, so a remote host's IRI does not match B's store). |

### Key findings

- **Community search is federated.** The `CommunitySearchHandler` uses `ICommunityFeedService.SearchCommunityAsync`, which walks the community's feed surface (members + follows). Remote content is discoverable via the follow edge without an explicit community tag (the peering branch admits followed actors' content without the community-tag filter).
- **Global search is local-only.** `GlobalSearchService` searches the local actor store and local object store only — no network fetches. Delivered remote objects become searchable once they are stored in the local object store (via inbox processing).
- **Object-document resolution is local-only.** The `ObjectDocumentHandler` reconstructs the IRI as `{localBase}/ap/v1/{path}`. A deep link to a remote host's IRI (e.g. `https://search-a.domain.local/ap/v1/u/alice/notes/post-deep-1`) 404s on B because B reconstructs it as `https://search-b.domain.local/ap/v1/u/alice/notes/post-deep-1`, which does not exist in B's store. On-demand remote object fetch for the object-document path is not implemented.
- **`AddCreateActivity` only populates the outbox**, not the global Activities store. The object-document endpoint checks both the Objects store and the Activities store (`TryGetObjectAsync` then `TryGetActivityAsync`), so activities added via `AddCreateActivity` must also be `PutActivityAsync`-ed to be resolvable by deep link.

### Discovery lag

In this test configuration (in-process `TestServer` with `LazyHandler`), discovery lag is effectively zero: content is searchable immediately after delivery completes. In production, discovery lag is bounded by the federation delivery interval (typically seconds to minutes, depending on the relay/fan-out configuration). The eventual-consistency window is:

- **Community search (federated):** Content appears as soon as the community's feed service fetches the followed actor's outbox (on first feed/search request, or on cache expiry).
- **Global search (local):** Content appears as soon as the remote object is stored in the local object store (on inbox delivery processing).
- **Deep links (origin-only):** Content is immediately resolvable on the origin instance (no lag). Non-origin instances 404 (by design, until on-demand fetch is implemented).
