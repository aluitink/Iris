# 157 — Post-interact, server-delivers: the outbox-published Block reaches the peer inbox, signed as the acting actor

> 2026-09-01 · Slice 19.6.3 (Phase 19.6 — Architectural expectations: client↔server interaction) · the client posts to its own outbox and the server does the cross-instance delivery, signed as the acting actor

## What was built

**19.6.3** asks: "The client posts (publishes) an activity to the outbox and the **server** performs
delivery to recipient inboxes (signed, per-actor), not the client. Verify: after a UI compose/follow/like,
the peer's inbox received the activity with a valid signature from the acting actor; the client's own
pipeline never made the cross-instance POST (inspect the delivery queue + peer logs/wire)."

The implementation already existed and was confirmed by reading the path:

- `OutboxPublishHandler` (`POST /ap/v1/u/{handle}/outbox`, `ActivityPubServerExtensions.cs`) records the
  activity in the actor's outbox, records the local edge, resolves the recipient(s), and calls
  `IDeliveryService.DeliverToActorAsync(recipientIri, activity, actorIri)` to schedule **server-side**
  delivery. For a `Block`, the recipient is the blocked actor's IRI directly (`RecordBlockLocalAsync`).
- `DeliveryService`/`DeliveryWorker` sign the outbound delivery as the **acting actor** (decision 029 —
  `DeliveryJob.ActorIri` → `X-Iris-Actor` header → `SigningHandler` resolves that actor's key), and POST
  to the recipient's inbox (resolving `endpoints.sharedInbox` if advertised, else the actor's inbox).
- The client (`Iris.Client.ActivityPubClient`) makes **only** a single signed POST to the actor's **own**
  outbox for every write (`FollowAsync`/`LikeAsync`/`BlockAsync`/`AnnounceAsync`/`PostNoteAsync`/… all
  call `DeliverAsync(actorId.OutboxOf(), activity)`) — it never addresses a recipient's inbox. This
  invariant was already pinned in `ActivityPubClientTests` (e.g. `FollowAsync_PostsFollowToTargetInbox_…`
  asserts the URI is the follower's own outbox, with the comment "the client never addresses a recipient's
  inbox").

What was missing was a **pin** for the single-recipient delivery path end-to-end. Follow, Create, and
Announce fan-out were already pinned by their own two-instance tests; the single-recipient `Block` (and
`Flag`) path was not. This slice adds that pin.

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — **unchanged** (`OutboxPublishHandler` +
  `RecordBlockLocalAsync` already record the block edge and return the blocked actor's IRI as the
  recipient; `DeliverToActorAsync` schedules the server-side delivery).
- `src/Iris.Server/Delivery/{DeliveryService,DeliveryWorker}.cs` — **unchanged** (already sign per-actor
  and POST to the recipient's inbox).
- `src/Iris.Client/ActivityPubClient.cs` — **unchanged** (already posts every write to the actor's own
  outbox; the invariant was already pinned in `ActivityPubClientTests`).
- `tests/Iris.Server.Tests/OutboxPublishServerDeliversIntegrationTests.cs` — **new** (two integration
  tests; see below).

## Tests

1252 → **1254** passing (+2: the two server-delivery integration tests).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean on the
changed file (the pre-existing whitespace violations in unrelated test files are untouched).

- `OutboxPublish_Block_ServerDeliversToBlockedActorInbox_SignedAsActingActor` — the central 19.6.3
  assertion: two instances, A (alice) and B (bob). Alice publishes a `Block` of bob to **A's own
  outbox** via a single signed POST (the client's write — it never addresses bob's inbox). A's server
  records the block in alice's outbox and delivers the `Block` to bob's inbox, signed as alice. B
  validates the signature (fetching alice's actor document from A) and records the alice → bob block
  edge. The edge is recorded **only if the signature validated as alice** — a wrong-actor or invalid
  signature would be rejected by B's signature gate and never recorded. This is the proof that the server
  (not the client) performed the cross-instance delivery, signed as the acting actor.
- `OutboxPublish_Block_DeliveredOnlyToTheBlockedActor_NotBroadcast` — the same `Block` is published, and
  a second local actor on B (carol) who is **not** the blocked target is asserted to receive nothing: the
  server delivered to bob's inbox only (a single directed delivery), not broadcast to every actor on the
  peer. This pins that the delivery is recipient-directed (per the recipient resolution), consistent with
  "the server performs delivery to recipient inboxes."

## Live verification (deferred — a live item)

The server-side delivery invariant is pinned by the new tests (the single-recipient Block path, end-to-end
over the wire with per-actor signing). The **live** half — driving compose/follow/like through the **UI**
in the two-instance Docker environment and confirming the peer's inbox received the activity with a valid
acting-actor signature (also covering **19.6.4**, the signature-identity expectation) — is the remaining
live-verification item for 19.6.3. It requires the two-instance Docker environment (dev1-public-host
unreachable from CI), so it is deferred as a live item; the server-side invariant it verifies is already
covered in CI by the new tests + the existing Follow/Create/Announce fan-out pins.

## Decisions

- **The pin uses the `Block` (single-recipient), not `Like` (owner-resolved).** A `Block`'s recipient is
  the blocked actor's IRI directly (`RecordBlockLocalAsync` returns `block.Object`), so the cross-instance
  delivery is unambiguous: A delivers to bob's inbox. A `Like`'s recipient is the *object's owner*
  (`object.attributedTo`), resolved from the author's object store when the object is local — for a remote
  note it falls back to the note IRI (whose "actor doc" cannot be fetched), and the inbound
  `LikeActivityHandler` records the edge only when the *liker* is local to the receiving instance (so a
  Like delivered to B records nothing on B, where alice is remote). The `Block` is therefore the cleanest,
  most faithful single-recipient pin of "the server delivers to the recipient's inbox, signed as the
  acting actor."
- **The "valid signature from the acting actor" is proven by the peer's record, not by re-validating in
  the test.** B's `InboxProcessor` gates every inbound activity on signature validation (fetching the
  acting actor's document + key, checking the `Signature` header). B records the block edge **only** after
  that gate passes for alice. So asserting the edge exists on B is the proof the delivery was signed as
  alice (resolvable from A's actor document) — re-implementing signature validation in the test would
  duplicate the gate rather than exercise it.
- **The "client never made the cross-instance POST" is proven by construction + the existing client pin.**
  The test's client write is a single signed POST to **A's own** outbox (the request is sent to A's
  TestServer, never B's). The general invariant — that *every* client write method posts to the actor's
  own outbox and never a recipient's inbox — was already pinned in `ActivityPubClientTests` (e.g.
  `FollowAsync_PostsFollowToTargetInbox_…` asserts the URI is the follower's own outbox). This slice
  therefore does not duplicate that pin; it focuses on the server-side half (the delivery A makes to B).
- **No production change.** The 19.6.3 invariant was already implemented (the outbox handler records +
  enqueues server-side delivery; the delivery worker signs per-actor; the client posts only to its own
  outbox). The slice is a verification pin, consistent with how 19.6.2 (change 156) and the 19.3.x
  federation slices were closed — the guarantee rests on existing behavior, now covered by a dedicated
  end-to-end test for the single-recipient path.
