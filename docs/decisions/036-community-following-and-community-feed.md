# 036 — Community following and community-feed membership model

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

A community is not a person. It has its own actor identity and its own local follow graph, but it is not a member of the person-follow store. Its content surfaces via a unified feed that merges the local members' outboxes, and it also has a follow set that determines which remote actors or communities it subscribes to.

The implementation is spread across [src/Iris.Server/FollowActivityHandler.cs](../../src/Iris.Server/FollowActivityHandler.cs), [src/Iris.Server/CommunityInboxActivityHandler.cs](../../src/Iris.Server/CommunityInboxActivityHandler.cs), and the community store in [src/Iris.Server.InMemory/InMemoryCommunityStore.cs](../../src/Iris.Server.InMemory/InMemoryCommunityStore.cs).

## Decision

Iris treats the community follow graph and the community member set as separate concerns:

- A community-to-follower edge is recorded in the community's follow set, not in the person-follow store.
- Following a community does not grant membership; membership remains a separate local relationship.
- Content delivered to a community inbox is recorded into each local member's outbox so the community feed can merge member content plus followed community content.

This keeps the semantics explicit: a community is a local actor with a follow graph and a feed, but it is not the same state as a person’s own follower list.

## Alternatives considered

### 1. Reuse the person follow store for communities

This would collapse two distinct models into one and would make it impossible to tell whether an edge is a personal follow or a community subscription.

### 2. Treat a community follow as membership

This is semantically wrong. A person can follow a community without becoming a member, and community membership is a local administrative concept distinct from the follow graph.

### 3. Route followed content through the community's own outbox only

This would not surface the content in the per-member feed model used by the project and would make the unified feed less coherent.

## Consequences

- The community follow graph remains a separate and explicit state set.
- The unified feed can merge community content and member activity without conflating the two.
- Remote content delivered to a community inbox can still surface in local members' feeds without granting membership.
- The follow semantics for communities are coherent with the rest of the protocol while staying specific to the community model.

## Code alignment

The current implementation matches the decision:

- `FollowActivityHandler` checks the community store before the actor-store resolution when deciding whether the recipient is local.
- `ICommunityStore.AddFollowAsync()` records a community follow edge.
- `CommunityInboxActivityHandler` records content in the local members’ outboxes to feed the community view.

This preserves a clean separation between community membership, community follow state, and content aggregation.
