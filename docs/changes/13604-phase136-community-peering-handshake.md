# 136.4 — Community peering handshake (Follow/Accept)

**Date:** 2026-09-14
**Slice:** 136.4 (Lemmy interop — peering handshake for communities)
**Status:** **COMPLETE (gated direction pinned).** The community Follow/Accept peering handshake is now
covered end-to-end across two instances in **both** directions: auto-accept (existing,
`CrossInstanceAcceptPropagationIntegrationTests`) and the **manually-approving (gated)** direction (new
this turn, `CommunityGatedPeeringIntegrationTests`). The Iris→Lemmy *live* leg remains blocked by the
Lemmy-side signature-parse + egress gap documented in 136.3, but the Iris-side handshake contract is now
fully pinned.

## What this slice delivers

136.4's acceptance criterion: *"both directional peering paths complete and persist across restart."*
The peering machinery was already built in earlier phases; this slice closed the **test-coverage gap** —
the one community direction that had no two-instance lock.

### What already existed (verified, not rebuilt)

- **Outbound community Follow** — both the owner-gated `POST /local/v1/c/{name}/follow/{targetIri}`
  (`CommunityFollowHandler`) and the AP-native `POST /ap/v1/c/{name}/outbox` publish
  (`CommunityOutboxPublishHandler` → `RecordCommunityFollowAsync`), delivered via
  `IDeliveryService.DeliverToActorAsync`.
- **Inbound community Follow** — `FollowActivityHandler`'s community branch records **two** edges
  (`ICommunityStore.AddFollowAsync` = the community follows the follower; `AddFollowerAsync` = the
  follower follows the community, populating `GET /c/{name}/followers`), surfaces the request in the
  community's outbox, and — when the community has `manuallyApprovesFollowers` — **suppresses the
  auto-Accept** (the gate, change 152 / 19.5.3).
- **Inbound Accept/Reject finalization** — `AcceptActivityHandler` / `RejectActivityHandler` with the G-3
  override (a local community is local even though it is not in the person store).
- **Community follow-decision path** — an operator `Accept`/`Reject` published to the community's outbox
  (`RecordCommunityDecisionAsync` → `RecordFollowDecisionLocalAsync` → `ApplyFollowDecisionEdgeAsync`,
  which idempotently confirms or removes the follows/followers edges) is server-delivered to the remote
  follower's inbox, signed as the community.
- **Durable edges** — `EdgeKind.CommunityFollow` / `CommunityFollower` survive restart (EF `EdgeEntity`
  table; file-backed `communities.json`; in-memory cleared by `Reset()`).
- **Auto-accept cross-instance** — `CrossInstanceAcceptPropagationIntegrationTests` (community branch).

### New this turn: the gated (manually-approving) community direction

**`tests/Iris.Server.Tests/CommunityGatedPeeringIntegrationTests.cs`** — a two-instance test
(A: manually-approving community `iris`; B: community `lumen`) that locks the held-then-Accepted
direction end to end:

1. **B's `lumen` follows A's `iris`** — lumen publishes `Follow(iris)` to B's outbox; it is delivered to
   A's `iris` inbox.
2. **A holds the follow** — `FollowActivityHandler`'s community branch records the follows + followers
   edges and surfaces the request in `iris`'s outbox, but **emits no Accept** (the gate suppresses the
   auto-Accept). The test asserts both halves: the edges are present on A (the provisional relationship
   exists locally) **and** no Accept has reached B's activity store after a settle window — the
   non-vacuous "held" signal.
3. **The operator Accepts** — the operator on A authors an `Accept` (actor = `iris`, object = the follow)
   and publishes it to `iris`'s own outbox (the AP-native follow-decision surface), signed as `iris`.
4. **The Accept round-trips** — A server-delivers the Accept to B's `lumen` inbox; B's
   `AcceptActivityHandler` (G-3) finalizes lumen's edge. The non-vacuous cross-instance artifact is B's
   activity store holding the inbound `Accept` (actor = `iris`, object = the follow) — which can only
   exist if A built AND delivered it and B stored it. A's edges are confirmed idempotently (no
   duplication).

**`tests/Iris.Testing/TestSeeder.cs`** — a new `SeedManuallyApprovingCommunityWithExistingKey` seeder:
the "existing key" form of the existing `SeedManuallyApprovingCommunityWithKey` (which generates a fresh
key). A shared two-host fixture seeds once with the key-generating form, then `Reset()`s and re-seeds
before each test — the re-seed must use the **same** key instance the fetchers/clients hold, so an
existing-key variant was required (mirrors `SeedCommunityWithExistingKey`).

## What is NOT in this slice

- No production source change — the gated community path already works; this turn **pins** it.
- No live Iris→Lemmy community follow — that leg is still blocked by the Lemmy-side signature-parse +
  egress gap (136.3) and the Lemmy→Iris WebFinger/egress gap (136.2), both documented as ops/Lemmy-side,
  not Iris code gaps. The Iris-side contract is what 136.4 pins.
- No dedicated `/local/v1/c/{name}/follow-requests` listing/decision endpoint (a person has
  `GET /local/v1/u/{handle}/requests`; a community surfaces inbound follows in its outbox and decides via
  an outbox `Accept`/`Reject` publish — the existing, tested mechanism). A symmetric community
  follow-request queue is a possible follow-up but out of scope for the handshake contract.

## Verification

- `CommunityGatedPeeringIntegrationTests`: **1 passed**.
- All 218 `Community*` server tests pass (no regression).
- Full fast suite: **Iris.Server.Tests 1159 passed, 0 failed** (was 1158, +1). All other projects green.
  The pre-existing load-induced flake (`Follow_Unfollow_Refollow_Cycle`) did not trigger this run.
