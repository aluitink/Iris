# Change 161b — Remove the Iris-only follow-decision endpoints (AP-native)

**Branch:** `feat/ap-native` (off `main` @ 140cdfc)
**Status:** complete — build clean, full suite green (1,245 tests, −24 from the 1,269 Phase A baseline; the
24 are the retired endpoint tests).

## What

The operator's follow Accept/Reject no longer has a dedicated route. The two Iris-only
`/follows/{**followId}` endpoints (person + community) and their handlers are **removed**; the
AP-native outbox path added in change 161a is now the **sole** way to accept/reject an inbound
follow. The sample UI's "Inbound follows" card was flipped to the outbox `AcceptAsync`/`RejectAsync`.

This is Phase B's first slice of the 19.0b rework. Per the operator's governing directive, the
*general flow* (follow, accept/reject, block, flag, like, delete, create, announce, undo,
follow/unfollow) is AP-native via the outbox; *specialized* capabilities (proxy relay, search,
feeds) stay as `iris:`-extension-discovered capabilities with their own transport (decisions D1–D4,
see `161-ap-native-rework-plan.md` §4). Mute/relay (D4a) are local, non-federated, and are **not**
AP activities — they keep a local (Basic-auth) transport and are moved off the core
`IActivityPubClient` in the next slice (Phase B.2).

## Why

The plan's directive: the AP client becomes a pure protocol layer, and every actor/community
activity flows through the actor's outbox. The follow-decision endpoint was the last
non-outbox write for a core-AP activity (an `Accept`/`Reject` *is* an ActivityStreams activity).
Removing it after change 161a proved the outbox path works end-to-end (including cross-instance
delivery). The deterministic IRIs and the local edge effect are unchanged — only the write
surface moved from a dedicated Basic-auth route to the outbox.

## What changed

### `src/Iris.Server/ActivityPubServerExtensions.cs`
- Removed the two route registrations
  (`/u/{handle}/follows/{**followId}` → `LocalFollowDecisionHandler`,
  `/c/{name}/follows/{**followId}` → `CommunityFollowDecisionHandler`) and their large comment
  blocks; replaced with a one-line note pointing to the outbox path.
- Removed `LocalFollowDecisionHandler`, `CommunityFollowDecisionHandler`, and the shared
  `HandleFollowDecisionCoreAsync` helper (~290 lines). The outbox path (`RecordFollowDecisionLocalAsync`,
  added in 161a) is the single remaining implementation of the accept/reject edge effect, used by
  both `OutboxPublishHandler` and `CommunityOutboxPublishHandler`.
- Fixed a stale `<see cref>` in `RecordFollowDecisionLocalAsync`'s doc that referenced the removed
  `LocalFollowDecisionHandler`.

The outbox contract (161a) now fully replaces the endpoint's: a decision is a signed outbox
publish; the deterministic IRI, the edge effect (accept ensures / reject removes the
follower→actor edge), and the server→follower delivery are all unchanged. The endpoint's
status-code matrix (409 target-mismatch, 403 local-follower, 410 not-recorded) is retired — the
outbox's total contract (202 record-what's-authored; 401 unsigned) governs instead.

### `samples/SampleBlazorClient/Pages/ActorDetail.razor`
- The "Inbound follows" card's Accept/Reject now call `Session.GetClient().AcceptAsync` /
  `RejectAsync(ActorIri, followIri)` (the AP-core outbox methods) instead of the Iris-only
  `AcceptFollowAsync`/`RejectFollowAsync`. The card's explanatory text now describes the outbox
  path. The now-unused operator-identity local was dropped.

### Tests
- **Retired** `OperatorRejectEndpointIntegrationTests.cs` (12 tests) and
  `CommunityFollowDecisionEndpointIntegrationTests.cs` (12 tests): they exercised the removed
  endpoints' route + Basic-auth + status-code matrix, which no longer exists. Their surviving
  coverage (accept records edge, reject removes edge, idempotency, 401-unauthenticated) is held by
  the 161a `OutboxFollowDecisionIntegrationTests.cs` and the repointed tests below.
- **Repointed** `OutboxSingleSourceOfTruthIntegrationTests.cs`: its `DecisionAsync` helper now
  publishes the deterministic `Accept`/`Reject` (via `FollowIris.BuildAccept`/`BuildReject`) to
  alice's **outbox** (signed) instead of Basic-auth POSTing to the removed endpoint. The test's
  "the outbox is the single source of truth" assertion now includes the two follow decisions as
  ordinary outbox-authored activities.
- **Repointed** `Security/FederationSignatureIntegrationTests.cs`
  (`ManuallyApprovingActor_OperatorRejectsFollow_RejectDeliveredBackAndRemovesEdge`): the operator
  reject is now a **signed** `RejectAsync` published to bob's outbox (the AP-native path) instead of
  a Basic-auth POST. This exposed a real test-wiring gap: the inbound key resolver
  (`RemoteInboundKeyResolver`) **always fetches the actor document — there is no local shortcut** —
  so for bob's (B-local) signed outbox publish to validate, B must be able to fetch bob's own actor
  document. The test's B fetcher was changed from a single-directional fetcher (peer-only) to a new
  `RoutingFetcher` (self→B, peer→A). The cross-instance assertion (the Reject federates back to
  alice over the wire, signed as bob, and removes alice's edge) is unchanged and still passes.

## Decisions

- **Retire the endpoint status-code matrix.** The legacy endpoint returned 409 (follow's target is
  not this actor), 403 (local follower), 410 (follow not recorded), 401 (unauthenticated). The outbox
  path does not replicate those: it is a total write surface — 401 (unsigned/invalid signature) or
  202 (recorded; the edge effect is a no-op when the referenced follow is unknown or foreign). This
  matches the outbox's existing behavior for an unknown `Undo` target (record what's authored). The
  operator-UX difference (a clear 409/410 vs. a silent 202 no-op) is acceptable because the sample
  UI lists only *actually-pending* inbound follows, so the operator never acts on an unknown follow.
- **B's fetcher must be routing (self + peer).** The inbound signature validator has no local key
  shortcut — it resolves *every* signing key by fetching the actor document. A two-instance test that
  posts a **local** actor's signed outbox activity must therefore give that instance a fetcher that
  can reach *both* itself (for its own local actor) and the peer (for the peer's activities). This is
  the same pattern the two-server `MutualFollowDeliveryLoopIntegrationTests` uses.

## Test counts

- Phase A baseline (161a): **1,269** passing.
- This slice: **1,245** passing (−24: the 24 retired endpoint tests). 0 failed, 0 skipped.
- Reconciliation: Server tests 782 → 758 (−24 = 12 OperatorReject + 12 CommunityFollowDecision).

## Remaining (Phase B.2 / C, not done here)

- **Phase B.2**: remove the `mutes/` + `relays/` local endpoints + handlers; move
  `MuteAsync`/`UnmuteAsync`/`SubscribeRelayAsync`/`UnsubscribeRelayAsync` (and the now-obsolete
  `AcceptFollowAsync`/`RejectFollowAsync` client methods) off the core `IActivityPubClient` into a
  `LocalModerationClient` (non-AP, Basic-auth). Flip the sample UI's Moderation card (Mute/Unmute) +
  relay card to it. Add the `proxy`/`mute`/`relay` `iris:capabilities` values (D1/D4a) for
  discovery.
- **Phase C**: correct the stale "InboxOf" doc comments on `IActivityPubClient`; sweep docs for
  removed-endpoint references.
