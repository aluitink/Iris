# 161c — Drop the dead follow-decision client methods

Phase 19.0b (AP-native rework), slice 19.0b.3 (client split), step 1 of 2.

## Why

Change 161b (Phase B.1) removed the two Iris-only follow-decision endpoints
(`/follows/{**followId}` person + community) and their handlers — the AP-native
outbox path (`AcceptAsync`/`RejectAsync`, change 161a) became the sole accept/reject
write. That left the client methods that *targeted* those endpoints as dead code:
`AcceptFollowAsync` + `RejectFollowAsync` (each with a `ProxyCredentials` overload)
and the private `LocalFollowDecisionAsync` helper they shared. Nothing called them —
the sample UI's "Inbound follows" card was already flipped to the outbox methods in
161b.

This step removes the dead surface so `IActivityPubClient` moves closer to a pure AP
protocol layer (the 19.0b.3 goal). It is the first of two 19.0b.3 steps: the next
creates a `LocalModerationClient` and moves the remaining local-moderation methods
(`MuteAsync`/`UnmuteAsync`/`SubscribeRelayAsync`/`UnsubscribeRelayAsync`) off the core
interface.

## What changed

- `IActivityPubClient` / `ActivityPubClient`: removed `AcceptFollowAsync` (2 overloads),
  `RejectFollowAsync` (2 overloads), and the `LocalFollowDecisionAsync` helper. The
  interface now exposes only AP-protocol methods (fetch, deliver, follow/undo,
  accept/reject via outbox, like/unlike, delete, block/unblock, flag/unflag, the
  collections, mute/relay writes, post note/reply, `SendAsync`).
- Test stubs: the 3 classes that implement `IActivityPubClient`
  (`IrisActorDocumentFetcherTests.StubActivityPubClient`,
  `IrisRemoteCollectionFetcherTests.StubCollectionClient`,
  `FeedServiceTests.StubClient`) had the 4 now-removed members deleted. No other test
  or sample referenced them.

## Decisions

- **Remove, don't move, the follow-decision methods.** Unlike mute/relay (which stay as
  local capabilities and move to `LocalModerationClient`), the follow-decision client
  methods targeted *removed* endpoints — they have no transport left, so they are simply
  deleted. The outbox `AcceptAsync`/`RejectAsync` already provide the AP-native
  equivalent.

## Impact

- Build: clean (`TreatWarningsAsErrors` on).
- Tests: 1,245 passing, 0 failed (no test-count change — these methods had no direct test
  coverage; they were exercised only through the endpoints removed in 161b).
- `samples/SampleBlazorClient/Pages/ActorDetail.razor` keeps component-*local* methods
  named `AcceptFollowAsync`/`RejectFollowAsync` (the `@onclick` handlers); they internally
  call the outbox `Session.GetClient().AcceptAsync`/`RejectAsync`, not the removed client
  methods, so they are unaffected.

## Remaining (next slice, 19.0b.3 step 2)

Create `ILocalModerationClient`/`LocalModerationClient` (Basic-auth, non-AP) holding
`MuteAsync`/`UnmuteAsync`/`SubscribeRelayAsync`/`UnsubscribeRelayAsync` (8 methods with
overloads), reusing the `LocalModerateAsync`/`LocalLocalDecisionAsync` helpers; remove
them from the core `IActivityPubClient`; flip the sample UI's Moderation + relay cards to
the new client; register it in DI; add tests.
