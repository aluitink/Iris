# 14827 — "All known" directory surfaces cached remote communities (S30 A8.2)

- **Status:** done (unit-verified; live re-verify deferred to QA)
- **Slice:** S30 (cross-instance community join/view blocked) — facet A8.2 (federated community discovery)
- **Related:** [14826](14826-community-document-serves-cached-remote-group.md) (S30 direct-view facet), S29 (community WebFinger, fixed `8af708c`)

## Problem

A remote community (Group) that this instance has cached during federation is persisted to the
durable **community store** by `RemoteCommunityPersister` (via the actor-document fetch path), but a
`Group` is not an `Actor`, so it never lands in the **actor store**. The Directory's "All known"
Communities tab — and the global Search page — read the actor store via
`GlobalSearchService` → `IAgentStore.SearchActorsAsync` / `IPersistenceProvider.Actors`, so a cached
remote community is invisible there. The result: on a peer instance B, a community A created
(`ii-comm`) is **never discoverable** for joining — the "All known" Communities tab is empty (the
exact S30 A8.2 symptom: *B cannot discover the remote community to join it*).

## Root cause

`GlobalSearchService.SearchPagedWithVisibilityAsync` builds the "All known" (mixed, `localOnly=false`)
actor pass exclusively from the actor store (`_persistence.Actors.SearchActorsAsync`). Remote
communities live in the community store, not the actor store, so they are omitted. The content pass
also does not surface them (a `Group` is a community, not a content object).

## Fix

In `GlobalSearchService` (`src/Iris.Server/Services/GlobalSearchService.cs`), the mixed actor pass now
merges the **cached remote community Groups** from the community store:

- New `GetCachedRemoteCommunitiesAsync(rawActors, query, ct)` enumerates
  `IPersistenceProvider.Communities.GetAllCommunityIrisAsync()`, drops local communities (IRI on the
  instance base — those belong to the Communities page's "All on this instance" tab, not the federated
  directory), de-duplicates against what the actor store already surfaced (and against itself), and
  keeps only communities that match the query (name / `preferredUsername` / IRI — the same
  case-insensitive substring rule the actor pass applies; an empty query matches everything).
- The merge runs only when `localOnly` is false (a remote community is, by definition, not on this
  instance); the "This instance" (localOnly) path is unchanged.
- The content pass now excludes stored `Group`s (`.Where(o => o is not Group)`), mirroring the
  existing exclusion of `Actor` objects, so a community is surfaced exactly once (by the actor pass)
  and never double-counted as content.

This makes the Directory "All known" Communities tab (and the Search page) list every cached remote
community, with its `FollowButton`/Join action already wired by `CommunityCard` → `ActorCard`.

## Tests

Three new tests in `tests/Iris.Server.Tests/Services/GlobalSearchServiceTests.cs`:

- `Search_AllKnownCommunities_SurfacesCachedRemoteGroup_NotLocal` — a local community + a cached
  remote community in the community store: "All known" (type=Actor, localOnly=false) lists the local
  actor + the cached remote community, **not** the local community; "This instance" (localOnly=true)
  lists only the local actor.
- `Search_AllKnownCommunities_QueryMatchesRemoteCommunityByName` — a query matching the cached remote
  community surfaces it; a no-match query returns empty.
- `Search_AllKnownCommunities_NoDoubleCounting_WhenCommunityAlreadyInActorStore` — a community in
  both the actor store and the community store surfaces exactly once.

## Verification

- `dotnet build`: 0 warnings / 0 errors.
- `dotnet test` (Iris.Server.Tests): 1424 pass / 0 fail (was 1421; +3 new tests).
- `dotnet test` (Iris.Server.Data.Tests, search): 3 pass / 0 fail (production EF store unaffected).
- Live re-verify deferred to QA (requires two-instance federation stack: A creates a community, B's
  "All known" Communities tab lists it, B follows it).

## Scope note

This fix addresses the **discovery** facet of S30 (A8.2). The direct-view facet is [14826](14826-community-document-serves-cached-remote-group.md);
the follow facet already works via the full-IRI actor page (S30 re-test 2026-09-21: A8.3 fixed). The
remaining S30 facet is **A8.4 community-post federation** (no UI to post into a community — the
compose surface offers only Public/Followers/Direct, no community selector), which is a separate,
larger surface and is not addressed here.
