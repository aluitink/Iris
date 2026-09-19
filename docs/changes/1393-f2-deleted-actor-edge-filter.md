# 139.3-F2 — Filter edges referencing a removed actor at read time

## Summary

A deleted local actor previously still surfaced through several edge read paths (followers, community
members, likers, announcers, dislikers, blockers, relays, join requests, moderation lists) because those
reads enumerate the `Edges` table (or the in-memory edge dictionaries) **without** consulting the actor
store. 139.3-F2 adds a read-path filter (not a DB sweep) that excludes an edge only when its source was a
*locally-provisioned-then-deleted* actor.

The filter is deliberately conservative: it drops an edge **only** when the source IRI was once provisioned
(stored as an actor) and is no longer present. Edges whose source is a **remote actor** (never stored on
this instance) or a **local actor that was never provisioned** are kept — both are legitimate, and the
earlier approach (an actor-join `EXISTS` / `ContainsKey`) wrongly dropped them.

## Behavior

- `GetFollowersAsync` / `GetFollowingAsync` (follow), `GetMembersAsync` / `GetJoinRequestsAsync` /
  `GetFollowersAsync` / `GetBlocksAsync` / `GetFlagsAsync` / `GetMutesAsync` (community),
  `GetLikersAsync` (like), `GetAnnouncersAsync` (announce), `GetDislikersAsync` (dislike),
  `GetBlockersAsync` / `GetFlagsAsync` / `GetMutesAsync` (moderation), `GetRelaysAsync` (relay):
  - Before deletion: the actor's edges appear (unchanged).
  - After `RemoveActorAsync(deletedActor)`: the deleted actor no longer appears in any of these lists.
  - A remote actor's (or never-provisioned local actor's) edges are unaffected.
  - Re-provisioning the same IRI (in-memory) clears the removed marker, so the edges surface again.

## Changes

### In-memory persistence (`src/Iris.Server.InMemory`)

- `Stores/InMemoryActorStore.cs`
  - Added a `_removed` concurrent set: populated in `RemoveActorAsync`, cleared in `PutActorAsync`
    (re-provision) and `Clear()`.
  - Replaced `ActorExists(Iri)` with `SourceSurvives(Iri)`:
    `!_removed.Contains(iri) || _actors.Contains(iri)` — hidden only when removed *and* gone.
- `InMemoryPersistenceProvider.cs`
  - The predicate wired into every in-memory edge store is now `iri => actorStore.SourceSurvives(iri)`
    (previously `ActorExists`, which over-filtered remote / never-provisioned sources).
- `Stores/InMemoryFollowStore.cs`
  - `GetFollowersAsync` now passes `filterSource: true` to `Snapshot` (it was missed; `GetFollowingAsync`
    already filtered).
- `Stores/InMemoryCommunityStore.cs`
  - `GetFollowersAsync` now applies `_sourceExists` (it was missed; `GetMembersAsync`, `GetJoinRequestsAsync`,
    and the block/flag/mute getters already filtered).
- The other in-memory edge stores (`InMemoryLikeStore`, `InMemoryAnnounceStore`, `InMemoryDislikeStore`,
  `InMemoryModerationStore`, `InMemoryRelayStore`) already applied the predicate in their read paths.

### EF Core / Postgres persistence (`src/Iris.Server.Data`)

- `Stores/EfActorStore.cs`
  - Added an in-process `_provisionedRemoved` set: populated in `RemoveActorAsync`.
  - Added `SourceSurvives(string sourceIri)` → `!_provisionedRemoved.Contains(sourceIri)`.
- `Stores/EdgeStore.cs`
  - Constructor now accepts an optional `Func<string, bool>? sourceSurvives` predicate.
  - `InSourcesAsync`, `InSourcesDescendingAsync`, and `OutTargetsAsync` accept
    `bool filterDeletedActors = false`; when true **and** a predicate is wired, the returned elements are
    filtered through the predicate. This replaced an earlier over-broad correlated subquery
    (`EXISTS (SELECT 1 FROM Actors WHERE Id = e.Source)`) that dropped remote and never-provisioned sources.
    `OutTargetsAsync` filters the returned *targets* (the actors, e.g. a community's members), not the
    source constant.
- `EntityFrameworkPersistenceExtensions.cs`
  - `EdgeStore` registration now wires `sourceSurvives: iri => EfActorStore.SourceSurvives(iri)`.
  - The concrete stores (`EfFollowStore`, `EfLikeStore`, `EfAnnounceStore`, `EfDislikeStore`,
    `EfRelayStore`, `EfModerationStore`, `EfCommunityStore`) pass `filterDeletedActors: true` on their
    read methods.

## Note on durability

The removed-set is **in-process** (in both backends). After a restart the set is empty and the durable
`Actors` table is the source of truth: a deleted actor has no row, so its edges are re-included. This is
acceptable — account deletion is local to a live instance, and the read-path filter's purpose is to keep a
*live* deleted actor from surfacing (e.g. in a member list rendered before the actor's content is
tombstoned), not to be a permanent durable tombstone. A permanent durable guarantee would require a
tombstone row, which is out of scope here.

## Tests

- `tests/Iris.Server.Tests/DeletedActorEdgeFilterIntegrationTests.cs` (new, in-memory) — 8 tests:
  deleted follower excluded from followers (remote kept), deleted liker excluded (remote kept), deleted
  announcer excluded, deleted member excluded (remote kept), deleted community-follower excluded,
  deleted blocker excluded, re-provisioned actor surfaces again.
- `tests/Iris.Server.Data.Tests/DeletedActorEdgeFilterTests.cs` (new, EF/Postgres) — 4 tests:
  deleted follower excluded (remote kept), deleted member excluded (remote kept), deleted liker excluded
  (remote kept), and an un-provisioned local actor is **not** excluded (guards against over-filtering).
- `tests/Iris.Server.Data.Tests/AccountDeletionRetentionTests.cs` (updated) — the "what survives" section
  now asserts a deleted local actor is **filtered** from the community member list (139.3-F2) rather than
  present; the like/follow edges from the *other* (live) actor still surface.

## Verification

- `dotnet build` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test tests/Iris.Server.Tests` — 1371 passed, 0 failed, 25 skipped.
- `dotnet test tests/Iris.Server.Data.Tests` — 20 passed, 0 failed.
