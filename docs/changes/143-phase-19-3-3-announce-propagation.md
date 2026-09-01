# 143 — Phase 19.3.3: Announce (boost) propagation across the two-instance network (fix)

> 2026-09-01 · Slice 19.3.3 · Phase 19 (federation, two-instance network) · commit `a0c86ad`

## What was built

Closed the **local-follower leg of Announce (boost) propagation**. A boost (an `Announce`) authored on
instance A is federated by A to its remote followers; when instance B (hosting a remote follower, e.g.
`bob`) receives the boost in `bob`'s inbox, B's `AnnounceActivityHandler` must surface the boost to
`bob`'s *local* followers (e.g. `carol`) on B. That leg was silently broken: the propagated copy was
built with the **receiving inbox's** IRI (`delivery.RecipientIri` = `bob`) for the deterministic Announce
IRI, but the deterministic IRI is scoped to the **announcer** (the activity's `Actor`, e.g. `alice`).
So the propagated copy got a *different* IRI (`{bob}/announces/{object}`) than the original boost
(`{alice}/announces/{object}`), never matched what the follower's outbox/activity-store expected, and the
local follower never saw the boost.

The fix scopes the deterministic IRI to the actual announcer and splits the propagation by follower
kind, mirroring `CreateActivityHandler`:

- **Local follower** — the boost is recorded in the follower's outbox **directly** (same-instance
  surfacing). No cross-instance delivery, and no re-entry into the follower's inbox: re-delivering the
  addressed copy to `carol`'s inbox would re-trigger `AnnounceActivityHandler` with `carol` as the
  recipient and fan the boost out to `carol`'s followers — the **boost loop** (the 19.3.2/19.3.3 failure).
- **Remote follower** — the boost is delivered to the recipient's **inbox** through the delivery queue,
  **signed by the announcer** (the canonical outbox → delivery-queue → recipient-inbox flow); the peer's
  handler records it in the follower's outbox on its instance.

## Key types & files

- `src/Iris.Server/Inbox/AnnounceActivityHandler.cs` — the propagation loop now builds the propagated
  form with `AnnounceIris.BuildAnnounce(announcerIri, objectIri, followerIri)` (scoped to the actual
  announcer) and records local followers in their outbox directly vs. delivers remote followers to their
  inbox. Docs updated to the corrected model.
- `tests/Iris.Server.Tests/AnnouncePropagationIntegrationTests.cs` — new two-instance integration test
  (2 tests).
- `tests/Iris.Server.Tests/Inbox/InboxProcessorTests.cs` — the Announce propagation unit test updated to
  the corrected model (local followers recorded in their outbox; the remote follower is the single
  delivery-queue job).
- `tests/Iris.Server.Tests/Security/FederationSignatureIntegrationTests.cs` —
  `Announce_LocalActorPropagatesToFollowersInbox_SignedWithAnnouncersKey` updated to assert the
  propagated form is recorded in the local follower's outbox (not re-stored under the deterministic IRI
  via a worker delivery).

## Tests

1193 → **1195** passing (the +2 are the new propagation tests). Full `dotnet test` green; `dotnet build`
clean (`TreatWarningsAsErrors`).

- `Boost_LocalNote_ReachesPeerLocalFollower_Once` — alice (A) boosts her own local note; A federates it
  to bob (the remote follower on B); B's handler propagates it to carol (bob's local follower). Asserts
  carol sees the boost **exactly once** (no amplification) and the total outbound re-fan-out from B is
  **bounded** (`≤ 4`) — the boost is not re-announced in a loop.
- `Boost_RemotePeerNote_CarriesObjectLink_NoInfiniteChain` — alice boosts bob's note (remote content).
  Asserts the stored boost references the remote object by **link** (not an embedded copy that would
  double-attribute), is attributed to alice (the announcer, not bob), and B's outbound re-fan-out stays
  bounded (no infinite announce chain).

## Decisions

- **Local-follower surfacing is a direct outbox record, not an inbox delivery.** The canonical
  outbox → delivery-queue → recipient-inbox flow is the *cross-instance* path. A local follower's inbox
  is on the *same* instance; re-delivering the addressed copy into that inbox would (a) require the
  delivery worker to loop back to its own host (which the two-instance test transport does not provide —
  it routes all outbound to the peer) and (b) re-enter the follower's inbox, re-triggering
  `AnnounceActivityHandler` with the follower as recipient and fanning the boost out again (the boost
  loop). Recording the boost in the local follower's outbox directly is the same-instance equivalent: the
  follower's followed-feed reads the outbox, exactly as `CreateActivityHandler` relies on for a local
  follower's visibility of a post.
- **The deterministic IRI is scoped to the announcer, not the receiving inbox.** The IRI
  `{announcer}/announces/{objectIri}` is defined by the *actor performing the boost* (the activity's
  `Actor`/`AttributedTo`), not by the inbox that happens to receive a copy. Using the announcer keeps the
  propagated form's IRI identical to the original boost's IRI, so the activity store and every follower's
  outbox reference the *same* activity (idempotent, dedup-friendly under the 19.3.1 add-if-absent guard).
  This is the one behavioral change from the pre-existing handler, which (incorrectly) keyed the IRI off
  `delivery.RecipientIri`.
- **Remote-follower delivery is signed by the announcer.** `DeliverToActorAsync(follower, propagated,
  recipientIri)` signs as the recipient (the announcer on the peer's instance), so the peer validates the
  boost against the announcer's key — matching the `CreateActivityHandler` remote-follower contract.
