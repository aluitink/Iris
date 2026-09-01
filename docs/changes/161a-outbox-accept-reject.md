# Change 161 (Phase A) — Outbox Accept/Reject (AP-native follow decision)

**Branch:** `feat/ap-native` (off `main` @ 140cdfc)
**Status:** complete — build clean, full suite green (1,269 tests, +9 from the 1,260 baseline).

## What

The operator's manual follow decision (an `Accept` or `Reject` of an inbound follow) now flows
through the **followed actor's outbox** — the AP-native write surface — instead of the legacy
Basic-auth follow-decision endpoint. The client authors the deterministic `Accept`/`Reject` and
publishes it to the followed actor's own outbox; the server records it, applies the local
follow-edge effect, and server-delivers it to the follower's inbox (signed as the followed actor).

This is the first slice of the Phase 19.0b AP-native rework (plan: `161-ap-native-rework-plan.md`).
It does **not** remove the legacy endpoints (that is Phase B); it adds the AP-native path alongside
them so the rework can proceed incrementally.

## Why

The plan's directive: all actor/community activities flow through the actor's outbox; the AP client
becomes a pure protocol layer. The follow Accept/Reject is the one activity that previously *had* to
go to a dedicated operator endpoint, so it is the keystone of the rework. Wiring it through the
outbox first proves the model before the broader removals.

## What changed

### `src/Iris.Server/ActivityPubServerExtensions.cs`
- `OutboxPublishHandler`: the activity switch gained `Accept` and `Reject` branches that call the new
  `RecordFollowDecisionLocalAsync` helper. The helper resolves the original `Follow` from the activity
  store (the decision's `object`), validates its target is the acting local actor, and:
  - **Accept**: ensures the follower→actor edge (person `IFollowStore` for a person target; the
    community's follows/followers sets for a local-community target).
  - **Reject**: removes the provisional follower→actor edge (same store dispatch).
  It returns the follower IRI so the handler's existing delivery step server-delivers the
  `Accept`/`Reject` to the follower's inbox (signed as the acting local actor).
- `CommunityOutboxPublishHandler`: the same two branches added (a community can accept/reject a follow
  made of it; the target is the community, so the community-store dispatch in the helper applies).
- New private helper `RecordFollowDecisionLocalAsync(persistence, actorIri, decision, accept, ct)`.

The deterministic IRIs are preserved: `{actorIri}/accepts/{followId}` / `{actorIri}/rejects/{followId}`
(`FollowIris.AcceptIri`/`RejectIri`), matching the legacy endpoint and the inbound handlers.

### `src/Iris.Client/ActivityPubClient.cs` + `IActivityPubClient.cs`
- New **AP-core** `AcceptAsync(Iri actorId, Iri followIri, ct)` and `RejectAsync(Iri actorId, Iri
  followIri, ct)`: build the deterministic `Accept`/`Reject` (object = the follow IRI, id =
  `{actorId}/accepts|rejects/{followId}`) and post it to `actorId.OutboxOf()` through the signed
  pipeline. These are the outbox replacements for the Iris-only `AcceptFollowAsync`/`RejectFollowAsync`
  (which hit the Basic-auth endpoint) and remain alongside them until Phase B removes the latter.

### Tests
- **New** `tests/Iris.Server.Tests/OutboxFollowDecisionIntegrationTests.cs` (6 tests): a single
  instance (bob, manually-approving, real key) + a remote alice. Asserts the signed outbox publish
  records the `Accept`/`Reject` under its deterministic IRI in the activity store + bob's outbox,
  ensures the edge on accept / removes it on reject, is idempotent on re-decision, 401s an unsigned
  publish, and records-but-applies-no-edge when the referenced follow is unknown (the outbox's
  record-what's-authored contract).
- **New** client tests in `tests/Iris.Client.Tests/ActivityPubClientTests.cs` (3 tests): `AcceptAsync`
  / `RejectAsync` post to the followed actor's own outbox with the correct type/actor/object/id
  (person + community target).
- **Updated** three test stubs (`IrisRemoteCollectionFetcherTests.StubCollectionClient`,
  `IrisActorDocumentFetcherTests.StubActivityPubClient`, `FeedServiceTests.StubClient`) to implement
  the two new interface members (the interface grew).

## Decisions

- **Outbox record-what's-authored contract.** Unlike the legacy endpoint (which 400/409/410s on a
  missing/foreign follow), the outbox **always records the authored activity** (202). When the
  referenced follow is unknown, the handler records the `Accept`/`Reject` but applies no local edge
  effect and has no delivery target (a no-op beyond the record). This matches the outbox's existing
  behavior for an unknown `Undo` target and keeps the write surface total. (The legacy endpoint's
  status-code semantics are preserved for the legacy path until Phase B removes it.)
- **Edge dispatch in one helper.** The person-vs-community edge store dispatch (a person edge in
  `IFollowStore`, a community edge in `ICommunityStore`'s follows/followers sets) lives in
  `RecordFollowDecisionLocalAsync`, shared by the person and community outbox handlers — mirroring
  `RecordFollowLocalAsync` and the legacy `HandleFollowDecisionCoreAsync`.

## Test counts

- Baseline (140cdfc, change 160): **1,260** passing.
- This slice: **1,269** passing (+9: 6 server outbox decision, 3 client). 0 failed, 0 skipped.

## Remaining (Phase B/C, not done here)

- **Phase B**: remove the `follows/` (accept/reject), `mutes/`, `relays/` Iris-only POST endpoints +
  handlers; switch the sample UI's "Inbound follows" card to `AcceptAsync`/`RejectAsync`.
- **Phase C**: split `AcceptFollowAsync`/`RejectFollowAsync`/`MuteAsync`/`UnmuteAsync`/
  `SubscribeRelayAsync`/`UnsubscribeRelayAsync` off the core `IActivityPubClient` into a
  `LocalModerationClient`; correct the stale "InboxOf" doc comments; sweep docs.
- **Open decisions D1–D4** (proxy relay, search, feeds, mute/relay modeling) still need an operator
  call before Phase B/C.
