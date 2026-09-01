# 161f — Doc sweep + stale inbox-comment fixes (19.0b.4)

## Summary

Phase 19.0b.4 of the AP-native rework: the final sub-slice. Two parts — (1) a doc sweep of the
compatibility matrix, live-evaluation checklist, and live-interop test plan to remove references to the
now-removed follow-decision endpoints (`/ap/v1/u/{handle}/follows/{followId}[/accept]`), and (2) fixing
the stale `InboxOf` doc comments in `IActivityPubClient` that still said activities were "delivered to
`targetId.InboxOf()`" / "the liker's own inbox" when the code (since the AP-native rework) publishes
every typed activity to `actorId.OutboxOf()` (the actor's own outbox).

## What changed

### Doc sweep (reference docs)

- **`docs/reference/COMPATIBILITY_MATRIX.md`** — §2 outbound-activities map: the `Accept` line now notes
  the Phase 19.0b outbox-publish path (the `OutboxPublishHandler`'s `Accept` branch) alongside the
  auto-accept path; the `Reject` line changes from "built but never sent" to "now sent (Phase 19.0b,
  AP-native)" with the outbox-publish mechanism. F3 row changes from `[GAP]` to `PASS-expected (Phase
  19.0b)`. Gap #2 changes from "No outbound `Reject`/`Undo`" to "now sent (Phase 19.0b, AP-native)."
- **`docs/reference/LIVE_EVALUATION_CHECKLIST.md`** — 19.1.2 waypoint: "Reject behavior (our
  local-follow-reject endpoint)" → "we publish a deterministic `Reject` to our outbox — AP-native, Phase
  19.0b; the removed follow-decision endpoint is gone."
- **`docs/reference/LIVE_INTEROP_TEST_PLAN.md`** — §0 setup: the follow-decision endpoint block
  (Basic-auth `POST …/follows/{followIri}/accept`) is replaced with the AP-native outbox-publish
  mechanism (HTTP-signed `POST …/outbox` with an `Accept`/`Reject` whose `object` is the follow IRI).
  F1 agent-acts: Accept via outbox `AcceptAsync`. F3: Reject via outbox `RejectAsync`. §4 G1/G3:
  community follow-decision via the community outbox `Accept` branch.
- **`docs/ROADMAP.md`** — 19.1.2 description: Accept/Reject updated to outbox-based. 19.6.2 outbox
  invariant: "plus Accept and Reject (published to the followed actor's outbox, AP-native Phase 19.0b;
  the legacy follow-decision endpoint is removed)." 19.0b.4 checkbox checked off.
- **`tests/Iris.Server.Tests/OutboxSingleSourceOfTruthIntegrationTests.cs`** — doc-comments: "the
  follow-decision endpoint's Accept/Reject" → "the operator publishes an Accept and a Reject (to alice's
  outbox, AP-native Phase 19.0b)."

### Stale `InboxOf` doc comments (`IActivityPubClient`)

Every typed method in `IActivityPubClient` now publishes to `actorId.OutboxOf()` (verified in
`ActivityPubClient.cs` — all 15 typed methods call `DeliverAsync(actorId.OutboxOf(), ...)`). The doc
comments still said "delivered to `targetId.InboxOf()`" / "the liker's own inbox" — stale since the
AP-native rework. Fixed:

- `DeliverAsync` — param renamed `inboxId` → `targetId`; doc updated to say "the target is typically the
  author's own outbox; the server owns the recipient hop."
- `FollowAsync` — "delivered to `targetId.InboxOf()`" → "published to `actorId.OutboxOf()`; the server
  records the follow edge and server-delivers the Follow to the target's inbox."
- `UndoFollowAsync` — "delivered to `actorId.InboxOf()` (the follower's own inbox)" → "published to
  `actorId.OutboxOf()` (the follower's own outbox); the server removes the local follow edge and
  server-delivers the Undo."
- `LikeAsync` — "delivered to the liker's OWN inbox (`actorId.InboxOf()`)” → "published to the liker's
  OWN outbox (`actorId.OutboxOf()`); the server records the like edge and server-delivers to the object's
  owner."
- `BlockAsync` — "delivered to `targetId.InboxOf()` (the blocked actor's inbox)" → "published to
  `actorId.OutboxOf()` (the blocking actor's own outbox); the server records the block edge and
  server-delivers to the target's inbox."
- `UnblockAsync` — "delivered to `targetId.InboxOf()`" → "published to `actorId.OutboxOf()`; the server
  removes the local block edge and server-delivers the Undo."
- `FlagAsync` — "delivered to `targetId.InboxOf()` (the flagged actor's inbox)" → "published to
  `actorId.OutboxOf()` (the flagging actor's own outbox); the server records the flag edge and
  server-delivers to the target's inbox."
- `UnflagAsync` — "delivered to `targetId.InboxOf()`" → "published to `actorId.OutboxOf()`; the server
  resolves the original flag and removes the recorded edge, then server-delivers the Undo."
- `PostNoteAsync` — "delivered to `actorId.InboxOf()` (the author's own inbox)" → "published to
  `actorId.OutboxOf()` (the author's own outbox); the server records it and federates to followers."
- `PostReplyAsync` — "delivered to `actorId.InboxOf()` (the author's own inbox)" → "published to
  `actorId.OutboxOf()` (the author's own outbox)."

The `UnlikeAsync` and `DeleteAsync` doc comments were already correct (they said "actor's own outbox
(`actorId.OutboxOf())`).

### `DeliverAsync` param rename

- `IActivityPubClient.DeliverAsync(Iri inboxId, ...)` → `DeliverAsync(Iri targetId, ...)`. Every caller
  passes an outbox (`actorId.OutboxOf()`), so the param name `inboxId` was misleading. The doc comment
  updated to say "the target is typically the author's own outbox."
- `ActivityPubClient.DeliverAsync` implementation: matched the param rename.
- 3 test stubs (`IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests`,
  `FeedServiceTests`): matched the param rename.

## Tests

No new tests — this is a doc/comment-only change. Full suite green: **1,254 tests, 0 failed**. Build
clean (`TreatWarningsAsErrors` on).

## Result

19.0b.4 complete. The AP-native rework (Phase 19.0b) is **done**: all five sub-slices (19.0b.1 through
19.0b.4) are checked off in the ROADMAP. The client is a pure AP protocol layer (every typed method
publishes to the actor's outbox); the local-moderation surface is split off into
`ILocalModerationClient` on the non-AP `/local/v1` tree; the follow-decision endpoints are removed
(the outbox is the sole write path); the reference docs are swept for the removed endpoints. **Next:
Phase 19.1 (live interop verification)** — 19.1.2 (F1) is unblocked.
