# 067 — F-24 community `followers` collection — closes F-24

> 2026-08-29 · Slice 12.22 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes gap **F-24** (a community's `followers` collection is always empty — the follows/followers inversion
was undocumented in the wire contract). AS2.0 defines a `followers` relationship on actors — the collection of
actors that follow a given actor. For a community (`Group`), a remote server checking the community's
`followers` collection expected to see the actors that follow the community. Iris recorded only the **follows**
edge (community → follower, so the follower's content reaches the community's feed) but **not** the inverse
**followers** edge (follower → community), so `GET /ap/v1/c/{name}/followers` always served an **empty**
collection — a conformance surprise.

The fix is to **record both edges** on an inbound follow and **remove both edges** on an un-follow: the
`ICommunityStore` now maintains a **followers set** (inverse of the follows set) per community, the
`FollowActivityHandler` records both edges (community → follower **and** follower → community) when an actor
follows a local community, and the `UndoActivityHandler` removes both edges when a local person un-follows a
local community. The `GET /ap/v1/c/{name}/followers` route now serves the followers set (previously hard-coded
empty).

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/ICommunityStore.cs` | Gained three methods: `AddFollowerAsync(community, follower)`, `RemoveFollowerAsync(community, follower)`, and `GetFollowersAsync(community)` — maintaining a followers set (inverse of the follows set) per community. |
| `src/Iris.Server.InMemory/InMemoryCommunityStore.cs` | Implements the three new methods with a `_followers` `ConcurrentDictionary<Iri, HashSet<Iri>>` (mirroring the existing `_follows` dict). |
| `src/Iris.Server/FollowActivityHandler.cs` | Community branch now calls `AddFollowerAsync(recipient, follower)` in addition to the existing `AddFollowAsync(recipient, follower)` — so an inbound follow records both the follows edge (community → follower) and the followers edge (follower → community). |
| `src/Iris.Server/UndoActivityHandler.cs` | Person branch now checks if the un-follow target is a local community and, if so, removes the person from **both** the community's followers set (`RemoveFollowerAsync`) and follows set (`RemoveFollowAsync`). |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | `CommunityCollectionHandler` for `GET /ap/v1/c/{name}/followers` now serves `GetFollowersAsync` (previously hard-coded empty). |
| `tests/Iris.Server.Tests/FollowActivityHandlerTests.cs` | 1 new unit test + 1 existing test extended: an inbound follow of a local community records **both** the follows edge and the followers edge. |
| `tests/Iris.Server.Tests/UndoActivityHandlerTests.cs` | 1 new unit test: a local person un-following a local community removes the person from **both** the community's followers set and follows set. |
| `tests/Iris.Server.Tests/CommunityEndpointIntegrationTests.cs` | 1 new integration test + 1 comment update: `GET /ap/v1/c/{name}/followers` returns an `OrderedCollection` listing the follower IRIs. |
| `tests/Iris.Server.Tests/CommunityFollowsCommunityIntegrationTests.cs` | 1 test update: after a community follows a remote community over the wire, the followed community's `followers` collection contains the follower's IRI. |

## Tests

847 → **850** (+3):

- `tests/Iris.Server.Tests/FollowActivityHandlerTests.cs` — 1 new unit test
  (`HandleAsync_LocalCommunity_RecordsFollowerInFollowersSet`): an inbound follow of a local community records
  **both** the follows edge (community → follower) and the followers edge (follower → community). The existing
  `HandleAsync_LocalCommunity_RecordsCommunityFollowAndSchedulesAccept` test was extended to also assert the
  followers edge.
- `tests/Iris.Server.Tests/UndoActivityHandlerTests.cs` — 1 new unit test
  (`HandleAsync_LocalPersonUndoesFollowOfLocalCommunity_RemovesCommunityFollowerAndFollow`): a local person
  un-following a local community removes the person from **both** the community's followers set and follows set.
- `tests/Iris.Server.Tests/CommunityEndpointIntegrationTests.cs` — 1 new integration test
  (`Followers_Page1_IsOrderedCollection_WithFollowerIris`): seeds the community's followers set directly (two
  remote followers) and asserts `GET /ap/v1/c/{name}/followers` returns an `OrderedCollection` listing both
  follower IRIs with `totalItems` = 2. The existing
  `Followers_IsEmpty_WhenCommunityHasNoFollowers` test was renamed to
  `Followers_IsEmpty_WhenNoActorFollowsTheCommunity` and its comment updated (it still passes — a community
  with no recorded followers serves an empty collection).
- `tests/Iris.Server.Tests/CommunityFollowsCommunityIntegrationTests.cs` — 1 existing test updated
  (`Community_FollowOfRemoteCommunity_AppearsInBothCommunitiesFollowing`): after B's community follows A's
  community over the wire, **A's community `followers` collection** now contains B's community IRI (the
  `FollowActivityHandler` on A recorded the inverse edge), and B's community `followers` remains empty (no
  actor has followed B's community in this test).

## Decisions

- **Record both edges on follow, remove both on un-follow.** The follows edge (community → follower) was
  pre-existing and drives the federated feed (the community fetches the follower's content). The followers edge
  (follower → community) is the F-24 addition and populates the community's `followers` collection. Recording
  both keeps the two sets consistent: a community that follows an actor also has that actor as a follower, and
  an un-follow removes the actor from both sets. This is the inverse of the person follows case (a person's
  `followers` set is maintained by the `FollowActivityHandler` via `IFollowStore`, and the person's `following`
  set is the inverse).
- **Only a local person's follow/un-follow of a local community records/removes the followers edge.** A remote
  follower's edge is owned by the remote instance (the remote instance records its own followers edge for the
  community). The community's local follows edge for a remote follower is already recorded by the
  `FollowActivityHandler`; the remote instance is responsible for recording the inverse edge on its own side.
- **The `followers` route serves the followers set, not a hard-coded empty.** The `CommunityCollectionHandler`
  now calls `GetFollowersAsync(community)` for the `followers` route (mirroring the `following` route, which
  calls `GetFollowsAsync(community)`). A community with no recorded followers still serves an empty collection
  (the `OrderedCollection` with `totalItems` = 0), so the endpoint shape is unchanged.
- **The `UndoActivityHandler` person branch checks for a local community target.** When a local person
  un-follows a target, the handler first resolves the target from the stored follow. If the target is a local
  community, the handler removes the person from both the community's followers set and follows set. If the
  target is a person (the pre-existing path), the handler removes the follow edge from `IFollowStore`. The two
  paths are mutually exclusive (a target is either a local community or not).

## Result

**F-24 is resolved.** A community's `followers` collection now lists its actual followers (populated by the
`FollowActivityHandler` on an inbound follow, removed by the `UndoActivityHandler` on an un-follow) instead of
being always empty. The `GET /ap/v1/c/{name}/followers` route serves the followers set. A remote server checking
a community's `followers` collection now sees the community's actual followers — a conformance fix.
