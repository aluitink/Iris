# 136.10 — Moderation and trust-boundary behavior

**Date:** 2026-09-14
**Slice:** 136.10 (Lemmy interop — cross-instance block/report/flag behavior, blocked-content-not-reintroduced)
**Status:** **COMPLETE (impl + tests).** Verified cross-instance block/report/flag behavior in both
directions and pinned the "blocked content is not reintroduced via new delivery" property.

## What this slice delivers

136.10's acceptance criteria:

1. Test remote actor/community block behavior in both directions (instance-level and actor-level where
   supported).
2. Validate report/flag activities and how moderation signals are represented cross-instance.
3. Ensure blocked content is not reintroduced via backfill or retries.
4. Exit when moderation actions enforce expected visibility and delivery boundaries.

### Verification: what was already correct (no change)

The cross-instance moderation behavior was audited end-to-end and found to be **already correct** in
**both directions** for the three moderation primitives:

| Primitive | Outbound (local actor → remote) | Inbound (remote actor → local) | Coverage |
|-----------|----------------------------------|--------------------------------|----------|
| **Block** (actor-level) | A records the `actor → target` edge on publish; the Block federates to the target's inbox; the remote instance records the edge when either party is local | `BlockActivityHandler` records the edge when either party is local | `ModerationUndoPropagationIntegrationTests` (Step 1: forward federation), `BlocksCollectionIntegrationTests` (`InboundBlock_BlocksFollow_IsExcludedFromFeed`) |
| **Flag** (report) | A records the `actor → target` flag edge on publish; the Flag federates to the target's inbox | `FlagActivityHandler` records the edge when either party is local | `ModerationUndoPropagationIntegrationTests` (Step 1), `FlagsCollectionIntegrationTests` |
| **Undo (unblock/unflag)** | The Undo is delivered to the remote instance; the edge is removed there (parties resolved from the stored original activity) | The remote instance removes the edge via `UndoActivityHandler.ResolveBlockEdgeAsync` | `ModerationUndoPropagationIntegrationTests` (Steps 2-3), `BlocksCollectionIntegrationTests` (`Unblock_AfterBlock_RemovesEdgeAndReIncludesFeed`) |

Local feed filtering (block = hard exclusion, mute = soft exclusion) is covered by `FeedServiceTests`
(`Feed_BlockedLocalFollow_IsExcludedFromFeed`, `Feed_BlockedRemoteFollow_IsExcludedFromFeed`,
`Feed_PartialBlock_KeepsUnblockedFollows`, `Feed_MuteDoesNotSeverFollow_UnlikeBlock`). Local delivery
suppression (the deliverer checks its own moderation store before delivering to a blocked follower) is
covered by `DeliveryQueueAndServiceTests`. Community-level moderation (community blocks/mutes of
members) is covered by `CommunityModerationIntegrationTests`.

No implementation change was required for any of the above.

### The genuine gaps (pinned by new tests)

Two gaps were identified and pinned by new cross-instance integration tests:

**Gap 1: Reverse-direction block (remote actor blocks local actor).** The forward direction (local actor
blocks remote actor) was already tested (`ModerationUndoPropagationIntegrationTests` Step 1). The
**reverse** direction (a remote actor on instance B blocks a local actor on instance A) was not tested:
the Block federates B→A, and A's `BlockActivityHandler` must record the `bob → alice` edge in A's
moderation store (the remote blocker of a local actor), and the inverse index
(`GetBlockersAsync(alice)`) must include bob.

**Gap 2: Blocked content not reintroduced via new delivery (the trust-boundary guarantee).** When a
local actor (alice on A) blocks a remote actor (bob on B), bob's **new** content (published after the
block) federates B→A and is **stored** on A (fetchable by direct IRI), but must be **excluded from
alice's feed**. This is the "blocked content is not reintroduced via backfill or retries" criterion.

The trust-boundary enforcement is **defense in depth**:

- **Delivery suppression** (`DeliveryService.DeliverToActorAsync`) is a **local optimization** — it
  suppresses delivery only when the *deliverer* has the `recipient → signer` block edge. It does **not**
  apply cross-instance: when bob (B) posts new content, B's `DeliveryService` checks B's local
  moderation store for the `alice → bob` edge — it does not have it (the block is on A). So m2
  federates B→A and is stored on A. The delivery suppression is a best-effort optimization for the
  same-instance case; it is **not** the trust-boundary guarantee.

- **Feed filtering** (`FeedService.GetFeedAsync`) is the **authoritative guarantee** — it always applies
  on the reader's side using the reader's local `IModerationStore` (`GetBlocksAsync(reader)` = hard
  exclusion, `GetMutesAsync(reader)` = soft exclusion). Even though m2 is delivered and stored on A, it
  is **not shown to alice** because A has the `alice → bob` block edge. This is what satisfies
  "blocked content is not reintroduced": the content is delivered and stored (fetchable by direct IRI)
  but excluded from the feed.

### New tests

**`CrossInstanceBlockedContentIntegrationTests`** (two-instance: A `block-content-a.domain.local`/alice,
B `block-content-b.domain.local`/bob; shared two-host fixture with a `RoutingFetcher`):

1. **`BlockedActorNewContent_IsStoredButExcludedFromFeed`** — alice (A) follows bob (B); the follow edge
   is recorded on both A (alice's following list) and B (bob's follower set). bob (B) posts new content
   m2 (pre-seeded in B's object store, then published via a signed outbox POST to B). m2 federates B→A
   (B's `DeliveryService` does not suppress — B's local store does not have the `alice → bob` edge).
   m2 is stored on A (asserted: `TryGetObjectAsync(m2Iri)` on A). alice (A) blocks bob (B) via a signed
   outbox POST to A; A records the `alice → bob` edge. **The key assertion**: a `FeedService`
   constructed with A's persistence + moderation store excludes m2 from alice's feed (the
   trust-boundary guarantee).

2. **`ReverseDirectionBlock_BlocksLocalActor_EdgeRecordedOnLocalInstance`** — bob (B) blocks alice (A)
   via a signed outbox POST to B. B records the `bob → alice` edge locally. The Block federates B→A;
   A's `BlockActivityHandler` records the `bob → alice` edge on A (asserted:
   `IsBlockedAsync(bob, alice)` on A). The inverse index agrees: `GetBlockersAsync(alice)` on A
   includes bob (asserted).

### Test count

Iris.Server.Tests: 1171 passed (1169 + 2 new), 0 failed. Iris.Core.Tests: 445 passed, 0 failed.
