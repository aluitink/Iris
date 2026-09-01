# 145 — Phase 19.3.5: Follow-edge convergence

> 2026-09-01 · Slice 19.3.5 · Phase 19 (federation, two-instance network)

## What was built

Pinned the **follow-edge convergence** invariant across two instances with a
follow / un-follow / re-follow cycle. After the cycle settles, both sides'
`IFollowStore` must agree on exactly the single `alice → bob` edge: no *orphan*
edge (an IRI in one side's collection with no counterpart on the other), no
*duplicate* edge (the same IRI listed more than once), and the public
`following`/`followers` collections are *stable* (re-reading yields the same
IRIs and the same count).

The lifecycle is two-sided, and each leg is exercised over the wire:

- **Follow.** `alice` (A) publishes a `Follow` to her own outbox; A federates it to
  `bob`'s inbox (B). B's `FollowActivityHandler` records the directed edge in B's
  follow store and schedules an `Accept` back to alice. A also records the follow
  it authored, so alice's own `following` set is populated on her home instance.
- **Un-follow.** `alice` publishes an `Undo` (object = the original `Follow`, by IRI)
  to her own outbox; A federates it to `bob`'s inbox (B). B's `UndoActivityHandler`
  (recipient = bob, the target) removes alice from bob's followers set; A's
  `UndoActivityHandler` (recipient = alice, the un-follower) removes alice's own
  following edge. This is the convergence-critical step: an orphan on *either* side
  means the cycle did not fully unwind.
- **Re-follow.** A fresh `Follow` (a new IRI, since the deterministic IRI was already
  stored) is federated again; B's `FollowActivityHandler` re-records the edge.

No production code changed. The `FollowActivityHandler` / `UndoActivityHandler` pair
already converged the edge correctly in both directions; this slice is a test that pins
the invariant and guards against a future regression that would (a) leave an orphan edge
on the target's followers set after an un-follow, (b) leave the follower's own following
edge, (c) record a duplicate IRI on a re-follow, or (d) serve an unstable
`following`/`followers` collection across re-reads.

## Key types & files

- `tests/Iris.Server.Tests/Delivery/FollowEdgeConvergenceIntegrationTests.cs` — new two-instance
  test `Follow_Unfollow_Refollow_Cycle_EdgesConvergeOnBothInstances_StableCollections`.
- `src/Iris.Server/Inbox/FollowActivityHandler.cs` — records the directed edge on inbound `Follow`
  and schedules the `Accept`. Unchanged.
- `src/Iris.Server/Inbox/UndoActivityHandler.cs` — removes the edge on inbound `Undo` of a follow,
  from both the target's followers set (recipient = target) and the follower's own following set
  (recipient = the un-follower). Unchanged.
- `src/Iris.Server.InMemory/Stores/InMemoryFollowStore.cs` — the `following`/`followers` stores are
  `HashSet<Iri>` (idempotent), so a re-follow cannot produce a duplicate IRI. Unchanged.

## Tests

1196 → **1197** passing (the +1 is the new convergence test). Full `dotnet test` green;
`dotnet build` clean (`TreatWarningsAsErrors`). The test is stable across repeated runs
(verified 5×) — each phase is awaited to settle on the *peer* side's store before the next
phase, so the delivery workers never race the assertions.

- `Follow_Unfollow_Refollow_Cycle_EdgesConvergeOnBothInstances_StableCollections` — two live
  in-process `TestServer` instances (A hosts alice, B hosts bob) wired over the wire (each
  instance's delivery transport routes to the peer; each fetcher routes by actor-IRI host so an
  inbound activity's signature is validated by fetching the author's actor doc from the right
  instance). The test drives the cycle by publishing signed `Follow` / `Undo` / `Follow`
  activities to alice's outbox through a hosted delivery worker (signed as alice) and awaits the
  peer-side store to settle after each leg. It then asserts:
  - **Both stores agree** — A records `alice → bob` and B records `alice → bob`, and nothing else
    (no reciprocal / spurious edge on either side).
  - **No orphan** — after the un-follow, both sides' edge is removed (bob's follower set on B and
    alice's following set on A are empty).
  - **No duplicate** — after the re-follow, each side's collection is exactly one IRI
    (`Assert.Single` + the IRI matches).
  - **Stable public collections** — the public `following` (alice, on A) and `followers` (bob, on B)
    endpoints, read through the live store (`?refresh=true` bypasses the collection-page cache),
    expose exactly the converged edge and are byte-stable across re-reads (same IRIs, same count).

## Decisions

- **The cycle is driven through the real outbox + federation path, not by seeding the stores.**
  Publishing each `Follow`/`Undo` to alice's outbox (signed as alice) and letting A's delivery
  worker federate it to bob's inbox exercises the full inbound pipeline on B (signature validation →
  store → `FollowActivityHandler`/`UndoActivityHandler`), which is the faithful model of how the
  edge is actually formed and removed in federation. Seeding `IFollowStore` directly (as
  `MutualFollowDeliveryLoopIntegrationTests` does for its loop topology) would skip the handlers and
  would not test the convergence the slice is about.
- **Each leg is awaited on the *peer* side's store before advancing.** The `Follow` is not
  "done" until B (bob's home instance) has recorded the edge; the `Undo` is not "done" until both
  A (alice's own following set) and B (bob's followers set) have dropped it. Awaiting the
  convergence-critical peer-side state (not merely the local publish 202) is what makes the
  orphan-edge assertions meaningful.
- **The public collections are read through the live store (`?refresh=true`), and the items are read
  from the raw JSON.** Two harness traps were avoided: (a) the `LocalCollectionPageCache` serves a
  cached page-1 document, so a read after the edge changes could otherwise return a stale empty page —
  `?refresh=true` bypasses the cache and re-renders from the store; (b) the ActivityStreams
  one-or-many converter emits a single item as a bare scalar, which the typed `OrderedCollection`
  deserializes as a string (losing the IRI), so the items are read from the raw JSON via
  `JsonDoc.GetItems`/`JsonDoc.ItemId` (the same path `CollectionEndpointIntegrationTests` uses).
- **A fresh `Follow` IRI is used for the re-follow.** The client mints a deterministic
  `{actor}/follows/{target}` IRI so a retried follow dedupes; but within a single test process the
  original follow is already stored under that IRI, so a re-follow with the same IRI would be deduped
  as a no-op. Using a `Guid`-suffixed IRI models a genuinely new follow activity (a new edge
  formation), which is what the re-follow half of the cycle is about.
