# 136.8 — Reactions and engagement interoperability (reactions, boosts, counters, graceful degradation)

**Date:** 2026-09-14
**Slice:** 136.8 (Lemmy interop — Like/UndoLike semantics, boost/Announce federation, counter convergence, graceful degradation of unsupported interaction types)
**Status:** **COMPLETE (impl + tests).** A local boost (Announce) of a **remote** object now federates to the
object's home instance, so the object's author's per-object boost counter (the `/shares` collection) sees the
Iris boost — even when the object's author is not a follower of the announcer.

## What this slice delivers

136.8's acceptance criterion: *"Validate Like/UndoLike semantics where supported; document any Lemmy vote
model mismatches. Verify counters and user-visible state converge eventually across both instances. Confirm
unsupported interaction types degrade gracefully (no crashes, clear logs). Exit when supported reactions interop
correctly and unsupported ones are safely ignored."*

### The core gap (why a cross-instance boost never reached the object's home)

The **Like** path already worked cross-instance: `RecordLikeLocalAsync` resolves the liked object's owner
(24.1, a remote fetch when the object is not stored locally), and the `OutboxPublishHandler`'s **generic
delivery block** delivers the Like to that owner when it is a remote (non-local) actor. So an Iris user's Like
of a Lemmy post reaches the Lemmy post's home (the author's `/likes` counter) — covered by
`LikeAnnounceUndoPropagationIntegrationTests.UndoLike_ServerDeliversToRemote_RecordsAndRemovesEdgeOnRemoteInstance`.

The **Announce** (boost) path did **not** work. The `OutboxPublishHandler`'s **Announce branch** is a separate
`else if` that fanned out the Announce **only to the announcer's followers + relays** and **discarded** the
owner that `RecordAnnounceLocalAsync` had resolved (it called `await RecordAnnounceLocalAsync(...)` without
capturing the return). Since the object's author is not (in general) a follower of the announcer, the follower
fan-out never carried the boost to the object's home — so a Lemmy author of a post that an Iris user boosted
never saw the boost, and the post's `/shares` collection (the per-object boost counter, decision 056 (d)) never
counted it.

This is the **136.7 reply-parent-author gap, now for the Announce path**: an outbound engagement activity of a
remote object whose author is not a follower was never delivered to the object's home. (The Like path was
already correct because it went through the generic delivery block; the Announce path had its own branch that
skipped it.)

### What changed

**`src/Iris.Server/ActivityPubServerExtensions.cs`**

- `OutboxPublishHandler` (Announce branch) — now **captures** the owner from
  `RecordAnnounceLocalAsync` (which resolves the announced object's owner via 24.1, a remote fetch when the
  object is not stored locally) and, after the follower + relay fan-out, **delivers the Announce to the owner**
  when it is a **resolvable remote (non-local) author** (`localActors.IsLocalActorAsync`). A local owner is a
  no-op (the boost is already on the same instance); the object-IRI fallback (owner unresolvable — a fetch
  failure) is skipped (an object IRI is not an actor and has no inbox — guarded by
  `owner.Value != announcedObjectIri.Value`). Mirrors the 136.7 reply-parent-author delivery. Best-effort: a
  delivery failure does not fail the publish (the follower/relay fan-out still ran).
- `RewriteOutboundAudienceAsync` (Announce case) — the Announce's `to` audience now names the **announced
  object's author** (a direct recipient of the boost), mirroring the 136.7 reply's parent-author audience. The
  object may be local (object store) or remote (fetched over the wire); best-effort.
- `ResolveReplyParentAuthorAsync` → renamed `ResolveObjectAuthorForDeliveryAsync` — the helper resolves an
  **object's** `attributedTo` author (local store, or a remote fetch), returning `null` when unresolvable. It is
  now used by **two** outbound paths (the 136.7 reply-parent and the 136.8 announce-owner); the name + doc
  reflect that general use. (Private method; one definition, two call sites.)

**`tests/Iris.Server.Tests/CrossInstanceAnnounceIntegrationTests.cs`** (new) — a two-instance harness
(A: `bob`, the object's home; B: `alice`, the announcer), reusing the shared two-host + bidirectional
federation wiring (`ActivityPubHostFactory.Create` with `Fetcher` / `DeliveryTransport` / `Client` overrides; a
`TestServerHolder` breaks the fetcher↔server circularity). Bob (A) stores the object note m1 (the Lemmy
stand-in's post, IRI in A's `/ap/v1` serving namespace). Alice (B) boosts m1 via a **signed
`POST /ap/v1/u/alice/outbox`** (the local-outbox publish path, where the audience rewrite + object-author
delivery live). B resolves m1's author (bob) by fetching m1 over the wire (B's `Client` → A), adds bob to the
Announce's `to`, and delivers the Announce to bob (A) via B's hosted `DeliveryWorker`; A validates alice's
signature (A's fetcher → B) and records the boost.

1. **`BoostOfRemoteObject_FederatesToObjectHome_AndIsCountedThere`** — asserts the full invariants on
   **A (the object's home)**:
   - **(a)** A recorded the `announcer → announced-object` edge (alice → m1) — the boost federated to the
     object's home (it was not stranded on B alone, where `RecordAnnounceLocalAsync` also recorded the edge for
     the local counter).
   - **(b)** A's announcers reverse index for m1 includes alice (the per-object boost counter, decision 056
     (d), on the object's home counts the Iris boost).
   - **(c)** `GET {m1}/shares` on A lists alice's boost (the item's `actor` is alice, `object` is m1 — the
     Lemmy author's per-object boost collection includes the Iris boost; the cross-instance boost is coherent
     on the object's home).

### What was already correct (validated, no change)

- **Like / Undo(Like) cross-instance** — the Like federates to the object's home (the generic delivery block +
  `RecordLikeLocalAsync` owner resolution); the Undo removes the edge on the object's home. Covered by
  `LikeAnnounceUndoPropagationIntegrationTests.UndoLike_ServerDeliversToRemote_RecordsAndRemovesEdgeOnRemoteInstance`
  (the note's `/likes` collection on the object's home carries the like; the Undo removes it). No change.
- **Undo(Announce) cross-instance** — the Undo(Announce) is delivered to the object's author (the 24.1
  resolution) and removes the edge on the object's home. Covered by
  `LikeAnnounceUndoPropagationIntegrationTests.UndoAnnounce_ServerDeliversToRemote_RemovesRecordedEdge` (this
  test seeds `bob follows alice`, so the original Announce reaches B via the follower fan-out; with the 136.8
  fix it ALSO reaches B via the object-author delivery — additive, no regression).
- **Graceful degradation of unsupported interaction types** — the inbox processor stores an activity with no
  registered handler (unknown activity types are preserved, not dropped) and dispatches nothing (no crash).
  Covered by `InboxProcessorTests.ProcessAsync_ActivityWithoutMatchingHandler_StoresActivityAndDispatchesNothing`.
  A Lemmy-specific vote/interaction type that Iris does not support is therefore safely ignored (stored, not
  interpreted) — the "safely ignored" half of the acceptance criterion.

## What is NOT in this slice

- **Lemmy vote-model mismatches** — Lemmy's vote model (upvote/downvote as `Vote` objects, not `Like`
  activities) is a Lemmy-side modeling choice. Iris speaks the ActivityStreams `Like`/`Announce` vocabulary;
  a conforming Lemmy receiver maps those to its vote model on ingest. Any residual mismatch is a Lemmy-side
  interpretation concern (documented here per the acceptance criterion's "document any Lemmy vote model
  mismatches"), not an Iris code gap. Iris's `Like`/`Announce` semantics are spec-compliant.
- **Live Iris↔Lemmy boost delivery** — the Lemmy side is still blocked by the Lemmy-side signature-parse +
  egress gap (136.3) and the Iris→Lemmy WebFinger/egress gap (136.2), both documented as ops/Lemmy-side, not
  Iris code gaps. The Iris-side cross-instance boost contract (a boost of a remote object federates to the
  object's home, is counted there, and the object's author is named in the `to` audience) is what 136.8
  implements + pins, against a synthetic remote peer (instance A) as the Lemmy stand-in.
- **Counter "convergence" beyond the object's home** — the per-object counters (`/likes`, `/shares`) are
  authoritative **on the object's home instance** (where the object is stored). A non-home instance that
  mirrors the object (e.g. via a proxy fallback) does not maintain an independent counter; it reads the
  object's home. 136.8 guarantees the home's counter converges (the edge is recorded exactly once per
  announcer, squashed across Announce/Undo/Announce re-boosts by the announcers reverse index). Cross-instance
  counter *replication* (a secondary instance maintaining its own count) is out of scope — the federation model
  is "the home is authoritative."
- No site-root JSON-LD (instance-level federation) — queued as a future candidate, out of scope here.

## Verification

- `CrossInstanceAnnounceIntegrationTests`: **1 passed** (the boost federates to the object's home A, is
  recorded in A's announcers reverse index, and is surfaced on A's `GET {m1}/shares`).
- **Non-vacuity check** — the test **fails** (times out after 30 s) when the Announce-branch object-author
  delivery is disabled (the boost never reaches A; only the local edge on B is recorded), confirming the test
  exercises the new code path, not a pre-existing behavior.
- `LikeAnnounceUndoPropagationIntegrationTests`: **2 passed** (the Like/Announce Undo paths are unaffected —
  the 136.8 fix is additive to the existing follower fan-out).
- `InboxProcessorTests`: **passed** (graceful degradation of unsupported interaction types).
- Full fast suite: **Iris.Server.Tests 1166 passed, 0 failed** (was 1165, +1 — no regression);
  **Iris.Core.Tests 445 passed, 0 failed**.
- Full solution build: **0 warnings, 0 errors** (`TreatWarningsAsErrors`).
- The pre-existing load-induced flake (`Follow_Unfollow_Refollow_Cycle`) did not trigger this run.
