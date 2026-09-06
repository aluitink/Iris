# Test Suite Review — Redundancy & Duplication Audit

Date: 2026-09-05

## Scope

This review covers all test projects under `tests/`:
`Iris.Core.Tests`, `Iris.Client.Tests`, `Iris.Client.Extensions.Tests`, `Iris.Server.Tests`,
`Iris.WebCrypto.Tests`, `Iris.LiveInterop.Tests`, `SampleServer.Tests`, and the shared
`Iris.Testing` harness library. `Iris.Server.Tests` is by far the largest project (~95 files)
and received the deepest scrutiny since its file names show the most surface-level similarity
(many `*PropagationIntegrationTests`, `*FanOutIntegrationTests`, `*CollectionIntegrationTests`
files).

**Overall conclusion: no exact/true duplicate tests were found anywhere in the suite** (no two
tests that assert the identical scenario with no distinguishing variable). The suite is
generally well-layered (unit vs. integration vs. end-to-end), and most apparent overlap is
intentional separation of concerns (e.g. a unit test for a state machine plus an integration
test proving the same state machine is wired correctly). However, a real, actionable pattern of
**structural/boilerplate redundancy** was found in `Iris.Server.Tests`: several small clusters of
files share a near-identical test harness/choreography and differ only in which ActivityStreams
verb or entity type is plugged in. These are candidates for consolidation via parameterized
(`[Theory]`) tests or a shared base fixture — not because the coverage is worthless, but because
the boilerplate cost of maintaining N nearly-identical files is high relative to the marginal
value of the Nth copy.

---

## 1. Redundancy clusters worth consolidating (`Iris.Server.Tests`)

### 1.1 Community follow/unfollow federation — 4 files, one shape

- [CommunityFollowingIntegrationTests.cs](../tests/Iris.Server.Tests/CommunityFollowingIntegrationTests.cs) — community follows a remote **person**
- [CommunityFollowsCommunityIntegrationTests.cs](../tests/Iris.Server.Tests/CommunityFollowsCommunityIntegrationTests.cs) — community follows a remote **community**
- [CommunityFollowsCommunityUnfollowPropagationIntegrationTests.cs](../tests/Iris.Server.Tests/CommunityFollowsCommunityUnfollowPropagationIntegrationTests.cs) — community follow/unfollow of a remote **community**
- [CommunityFollowsPersonUnfollowPropagationIntegrationTests.cs](../tests/Iris.Server.Tests/CommunityFollowsPersonUnfollowPropagationIntegrationTests.cs) — community follow/unfollow of a remote **person**

All four use the identical choreography: seed a two-instance topology → community publishes a
signed `Follow`/`Undo` → poll (`TestFederation.WaitForAsync`) for the edge to appear/disappear on
the remote instance → assert content propagation into the feed. The only variable across the 4
files is "target is a person vs. a community" and "follow vs. unfollow". This is effectively a
2×2 matrix expressed as 4 separate files/classes rather than one parameterized fixture.

**Recommendation:** fold into two files (`CommunityFollowsPerson*`, `CommunityFollowsCommunity*`),
each covering follow + unfollow with a shared private helper for the wait/assert choreography, or
a single `[Theory(MemberData)]` keyed on target-entity-type.

### 1.2 Cross-instance Accept/Reject propagation — 2 files, same 2×2 matrix

- [CrossInstanceAcceptPropagationIntegrationTests.cs](../tests/Iris.Server.Tests/CrossInstanceAcceptPropagationIntegrationTests.cs) — `PersonFollowOfRemoteActor_AutoAcceptPropagatesBackToFollowerHomeInstance`, `CommunityFollowOfRemoteCommunity_AutoAcceptPropagatesBackToFollowerHomeInstance`
- [CrossInstanceRejectPropagationIntegrationTests.cs](../tests/Iris.Server.Tests/CrossInstanceRejectPropagationIntegrationTests.cs) — `PersonFollowOfRemoteActor_ThenReject_...`, `CommunityFollowOfRemoteActor_ThenReject_...`

Each file already internally parameterizes person-vs-community; the two files are themselves
parallel on Accept-vs-Reject. Reasonable to keep as 2 files (Accept/Reject is a meaningful
behavioral split — one grows an edge, one guarantees no edge/removes one) — flagged here as
**minor** redundancy only, lower priority than 1.1.

### 1.3 Cross-instance "undo propagation" trio — near-identical wait/assert harness

- [LikeAnnounceUndoPropagationIntegrationTests.cs](../tests/Iris.Server.Tests/LikeAnnounceUndoPropagationIntegrationTests.cs) (`UndoLike_...`, `UndoAnnounce_...`)
- [ModerationUndoPropagationIntegrationTests.cs](../tests/Iris.Server.Tests/ModerationUndoPropagationIntegrationTests.cs) (`UndoBlock_...`, `UndoFlag_...`)
- [MuteUndoPropagationIntegrationTests.cs](../tests/Iris.Server.Tests/MuteUndoPropagationIntegrationTests.cs) (`Mute_AndUndo_...`)

All three follow the identical two-step choreography — publish activity X on instance A, wait for
B to record the edge, publish `Undo`, wait for B to remove the edge — differing only in which
verb (`Like`/`Announce`/`Block`/`Flag`/`Mute`) is plugged in. The wait/assert blocks are
structurally line-for-line copies with a different store predicate swapped in
(`HasLikedAsync` / `IsBlockedAsync` / `IsMutedAsync`, etc.).

**Recommendation:** extract the shared "publish → wait for edge → undo → wait for removal"
choreography into a helper in `Iris.Testing`, then express each verb as a one-line `[Theory]`
case or a short test method that only supplies the verb-specific store predicate. Coverage is
legitimate (each verb has independently reachable code in the inbox handlers), but 3 files of
near-duplicate scaffolding is unnecessary maintenance surface.

### 1.4 Moderation-style collection reads — repeated lifecycle boilerplate

- [FlagsCollectionIntegrationTests.cs](../tests/Iris.Server.Tests/FlagsCollectionIntegrationTests.cs)
- [LikedCollectionIntegrationTests.cs](../tests/Iris.Server.Tests/LikedCollectionIntegrationTests.cs)
- [MutesCollectionIntegrationTests.cs](../tests/Iris.Server.Tests/MutesCollectionIntegrationTests.cs)
- [BlocksCollectionIntegrationTests.cs](../tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs)

Each tests the same lifecycle for a different edge type: actor document advertises the
collection → collection is an empty `OrderedCollection` before any activity → an inbound activity
records the edge → the collection endpoint reflects it → typed client can read it. e.g.
`FlagsCollectionIntegrationTests`'s `ActorDocument_AdvertisesFlagsCollection` and
`MutesCollectionIntegrationTests`'s `ActorDocument_AdvertisesMutesCollection` are structurally
identical, differing only in the extension property name being asserted.

**Recommendation:** low priority — each file does pin a genuinely different collection
(`flags`/`liked`/`mutes`/`blocks` are different persistence stores and different handlers), so
this is more "boilerplate" than "wasted coverage". If touched again, consider a shared
`CollectionLifecycleTestBase` or table-driven test to cut duplication, but not urgent.

### 1.5 Collection paging assertions repeated across many unrelated endpoints

`CollectionEndpointIntegrationTests`, `CommunityOutboxCollectionIntegrationTests`,
`BlocksCollectionIntegrationTests`, `CommunityEndpointIntegrationTests` (`Members_Page1_*`,
`Members_Page2_*`), `CommunityFeedCorrectnessIntegrationTests` (`Feed_Page2_*`),
`CommunitySearchIntegrationTests` (`Search_Page2_*`) all separately re-assert the same
`OrderedCollection` → `OrderedCollectionPage` paging contract (page 1 is a plain
`OrderedCollection`, page N has `prev`/`next`/`partOf`/`totalItems`). This is the generic
collection-paging contract tested ~6+ times against different endpoints.

**Recommendation:** lowest priority / optional. Each endpoint does need at least one paging test
to prove its handler wires paging correctly, so this isn't truly redundant, but a shared paging
assertion helper (`AssertIsPageOne(doc)`, `AssertIsPageTwo(doc, prev, next, total)`) in
`Iris.Testing` would remove the copy-pasted assertion blocks currently duplicated in each file.

### 1.6 `CommunityMembershipClientIntegrationTests` vs `CommunityMembershipManagementIntegrationTests`

- [CommunityMembershipClientIntegrationTests.cs](../tests/Iris.Server.Tests/CommunityMembershipClientIntegrationTests.cs) — drives `IActivityPubClient.AddMemberAsync`/`RemoveMemberAsync`
- [CommunityMembershipManagementIntegrationTests.cs](../tests/Iris.Server.Tests/CommunityMembershipManagementIntegrationTests.cs) — drives the same Add/Remove primitives via raw signed HTTP POST, plus extra feed/members-collection reflection assertions

The `Client` variant's own XML doc comment says it is explicitly "the client-side counterpart to
`CommunityMembershipManagementIntegrationTests`" — this is **intentional layering** (client API
surface vs. wire-level behavior), not an accidental duplicate, so no action needed. Noted here
only because the two class names and bodies are similar enough to look like a duplicate at a
glance.

### 1.7 `FederationEd25519SignatureIntegrationTests` — thin single-test file

[FederationEd25519SignatureIntegrationTests.cs](../tests/Iris.Server.Tests/Security/FederationEd25519SignatureIntegrationTests.cs)
contains one test (`Resolver_ResolvesRemoteEd25519Key_ByFetchingActorDocumentOverWire`) that
exercises the same follow/accept federation loop as
[FederationSignatureIntegrationTests.cs](../tests/Iris.Server.Tests/Security/FederationSignatureIntegrationTests.cs),
merely swapping the key algorithm to Ed25519. Low-value as a standalone file.

**Recommendation:** fold as an additional `[Theory]` case (algorithm parameter) into
`FederationSignatureIntegrationTests`, or at minimum keep but note it exists purely to cover the
Ed25519 key-resolution path.

---

## 2. Overlap that looks suspicious but is legitimate (no action needed)

These were investigated and found to be **intentional separation of concerns** (unit vs.
integration, or two genuinely distinct code paths), not redundant:

| Files | Why it's not redundant |
|---|---|
| `Delivery/CircuitBreakerUnitTests` vs `Delivery/CircuitBreakerIntegrationTests` | Unit drives the breaker state machine directly; integration proves it's wired into the real `DeliveryWorker`. |
| `Delivery/DeliveryRetryTests` vs `Delivery/DeliveryIntegrationTests` | Retry/backoff policy in isolation vs. full over-the-wire federation loop. |
| `Delivery/DeliveryWorkerConcurrencyTests` vs `Delivery/DeliveryWorkerRateLimitTests` | Distinct constraints (parallelism bound vs. per-peer throttling). |
| `Observability/DeliveryMetricsUnitTests` vs `Observability/DeliveryMetricsIntegrationTests` | Counter logic vs. counters accruing through a real worker. |
| `Observability/HealthCheckUnitTests` vs `Observability/HealthEndpointIntegrationTests` | Health-check logic in isolation vs. HTTP endpoint/status-code contract. |
| `Security/KeyRotationInvalidationTests` vs `Security/KeyRotationFederationIntegrationTests` | Cache-invalidation decision logic vs. one end-to-end proof it's wired up. |
| `Security/OutboundSignatureConformanceTests` vs `Security/FederationSignatureIntegrationTests` | Outbound signing conformance vs. inbound validation — opposite directions of the same handshake. |
| `Caching/IrisRemoteCollectionFetcherTests` vs `Caching/RemoteCollectionFetcherIntegrationTests` | Stub-client unit test vs. real signed HTTP round trip. |
| `Services/FeedServiceTests` vs `Services/CommunityFeedIntegrationTests`/`CommunityFeedRemoteMemberIntegrationTests`/`FollowFeedIntegrationTests` | Pure feed-merge/filter logic (mocked resolvers) vs. end-to-end endpoint behavior with real stores. |
| All 16 files in `Inbox/*ActivityHandlerTests.cs` | Each covers one ActivityStreams verb's handler; no cross-handler duplication found. |
| `Iris.Core.Tests/Caching/*` vs `Iris.Client.Tests/Caching/*` | Core tests the shared `CachingReadThrough<T>` engine once; Client tests concrete cache wiring/façades. `ClientCacheTests.cs` explicitly documents this split in a comment. |
| `Iris.Core.Tests/Identity/{KeyPairGeneratorTests, KeyPairTests, Ed25519KeyTests, KeyPemTests}` | Factory/generation, sign-verify+JWK round trip, Ed25519-specific (RFC 8037), and PEM I/O are four distinct concerns, not overlapping. |
| `Iris.Core.Tests/Signing/{HttpSignatureTests, SignatureHeaderTests}` vs `Iris.Client.Tests/Pipeline/SigningHandlerTests` | Signer/verifier contract vs. header parsing vs. `DelegatingHandler` integration. |
| `SmokeTests.cs` in `Iris.Core.Tests`, `Iris.Client.Tests`, `Iris.Server.Tests` | Each is scoped to its own project (constant check / single in-process instance / two-instance federation) — same file name, unrelated bodies. |
| `Iris.Client.Tests/Caching/CacheWiringTests` (`GetActorAsync_WithActorCache_SecondReadHitsCache`) vs `ClientServerIntegrationTests` (`GetActor_ServedFromCacheOnSecondRead`) | Both check "second read is a cache hit", but one uses a fake counting handler (fast unit test) and the other exercises the real server pipeline end-to-end. Minor duplication in intent, acceptable given the very different execution cost/scope. |
| `Delivery/*` (Move) `MoveFederationIntegrationTests` vs `MoveKeyRotationIntegrationTests` vs `MoveExtensionRoundTripIntegrationTests` | Core re-pointing, key-rotation cache invalidation, and extension-data preservation are three distinct invariants of `Move`. |
| `Mastodon*InboundIntegrationTests` trio | `MastodonExtensionPassthroughIntegrationTests` tests direct store/read (no signature pipeline); `MastodonPollInboundIntegrationTests`/`MastodonSensitiveFlagInboundIntegrationTests` go through the signed inbound pipeline — different code paths. |
| `OutboxAudienceMatchIntegrationTests` vs `OutboxAudienceMetadataIntegrationTests` | "Who receives the delivery" vs "what `to`/`cc` the wire payload carries" — delivery targeting vs. wire-format correctness. |
| `LikedCollectionIntegrationTests` vs `InteractionCollectionIntegrationTests` | Liked collection is on the *liker's* actor document; interaction (likes/shares) collections are on the *object's* document — inverse endpoints, not duplicates. |
| `Delivery/InboundTombstoneIntegrationTests` vs `Delivery/ObjectPropagationIntegrationTests` | Receiving a tombstone from a remote author vs. sending an update/delete to remote followers — opposite directions of federated delete. |
| `Outbox{Create,Announce}FanOutIntegrationTests` vs `{Outbox}RelayFanOutIntegrationTests` | Different recipient-resolution rules per activity verb, and inbox-entry vs. outbox-entry relay paths — distinct code paths despite similar helper chains. |

---

## 3. Other projects (`Iris.Client.Tests`, `Iris.Core.Tests`, `Iris.Client.Extensions.Tests`, `SampleServer.Tests`, `Iris.LiveInterop.Tests`, `Iris.WebCrypto.Tests`)

No true duplicates or actionable redundancy clusters were found. These projects are smaller and
already show clear separation by layer (Core = pure logic/serialization, Client = HTTP/business
logic, Extensions = high-level workflow smoke tests, SampleServer.Tests = sample-hosting
concerns). The one theoretically mergeable case
(`CacheWiringTests.GetActorAsync_WithActorCache_SecondReadHitsCache` vs.
`ClientServerIntegrationTests.GetActor_ServedFromCacheOnSecondRead`, noted above) is minor and not
worth acting on given the cost/scope difference between the two tests.

---

## 4. Summary of recommendations, by priority

| Priority | Action | Files affected |
|---|---|---|
| Medium | Extract shared "publish → wait for edge → undo → wait for removal" helper; consider `[Theory]` | `LikeAnnounceUndoPropagationIntegrationTests`, `ModerationUndoPropagationIntegrationTests`, `MuteUndoPropagationIntegrationTests` |
| Medium | Consolidate the community follow/unfollow 2×2 matrix into 2 files (by target type) instead of 4 | `CommunityFollowingIntegrationTests`, `CommunityFollowsCommunityIntegrationTests`, `CommunityFollowsCommunityUnfollowPropagationIntegrationTests`, `CommunityFollowsPersonUnfollowPropagationIntegrationTests` |
| Low | Fold single-test Ed25519 file into the main signature integration suite as a `[Theory]` case | `Security/FederationEd25519SignatureIntegrationTests`, `Security/FederationSignatureIntegrationTests` |
| Low | Optional: shared paging-assertion helper to cut copy-pasted `OrderedCollectionPage` assertions | `CollectionEndpointIntegrationTests`, `CommunityOutboxCollectionIntegrationTests`, `BlocksCollectionIntegrationTests`, `CommunityEndpointIntegrationTests`, `CommunityFeedCorrectnessIntegrationTests`, `CommunitySearchIntegrationTests` |
| Low | Optional: shared collection-lifecycle test base for advertise/empty/record/read pattern | `FlagsCollectionIntegrationTests`, `LikedCollectionIntegrationTests`, `MutesCollectionIntegrationTests`, `BlocksCollectionIntegrationTests` |
| None | No action — intentional layering | All entries in section 2 |

None of the findings above indicate wasted or incorrect test coverage — the recommendations are
about reducing boilerplate/maintenance surface in `Iris.Server.Tests`, not removing coverage.
