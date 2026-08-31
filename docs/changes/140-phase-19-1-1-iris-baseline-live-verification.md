# 140 — Phase 19.1.1: Iris↔Iris baseline (live verification)

## What was done

Executed the 19.1.1 baseline over the live Docker stack (iris-dev1 ↔ iris-dev2, both on public FQDNs).
Used the IrisSigner helper (driven via `docker exec`) to make signed ActivityPub requests against both
instances. All checks are wire-level (no browser UI in this session).

## Results

| Check | Status | Notes |
|---|---|---|
| Follow (dev1→dev2) | PASS | Seeded edge; both `following`/`followers` consistent |
| Follow (dev2→dev1) | PASS | Signed POST → 202; edge recorded on dev1's `followers` |
| Accept round-trip | PASS (with note) | Accepts delivered (in-memory queue drained, health check "empty"); not in outboxes (by design: response, not authored activity) |
| Unfollow via Undo | PARTIAL | `following` edge removed on unfollower's side; `followers` on followed side NOT updated |
| Like | PASS | Recorded in `liked` collection; delivered to peer |
| Post (Create Note) | PARTIAL | Note delivered to peer's objects store (fetchable by IRI); **outbox shows 20 duplicate copies of the same Create** |
| Community follow | PARTIAL | Local `following` edge recorded on dev1; delivery to dev2's community inbox pending/lost; dev2's community `followers` empty |
| Community post surfacing | NOT TESTED | Dependent on community follow completing on the peer |

## Findings (→ 19.4)

### F-1911-1: Undo (unfollow) does not update the followed side's `followers` set — FIXED

**Repro:** alice-dev2 follows alice-dev1 (edge on both sides). alice-dev2 publishes an `Undo` of the
Follow. After delivery: alice-dev2's `following` no longer lists alice-dev1 (correct), but
alice-dev1's `followers` still lists alice-dev2 (stale).

**Wire evidence:**
- `GET iris-dev1:8081/ap/v1/u/alice/followers` → still contains `https://iris-dev2.luit.ink/ap/v1/u/alice`
- `GET iris-dev2:8082/ap/v1/u/alice/following` → no longer contains `https://iris-dev1.luit.ink/ap/v1/u/alice`

**Root cause:** The outbox publish handler delivers an Undo of a Follow to the **target's** inbox
(the followed side), so the recipient is the target — not the un-follower. The `UndoActivityHandler`
assumed the recipient was always the un-follower and tried to remove the target's follow edge (wrong
direction). The follower's own following edge was removed on the follower's home instance by
`RemoveFollowLocalAsync`, but the inverse edge on the target's followers set was never removed.

**Fix:** `UndoActivityHandler` now checks whether the recipient is the target. When the recipient is
the target (the normal case for an outbox-published Undo), the handler removes the follower from the
target's followers set — the inverse edge. When the recipient is the un-follower (direct inbox
delivery), the handler removes the un-follower's own following edge (existing behavior preserved).

**Tests:** `HandleAsync_UndoDeliveredToTarget_RemovesFollowerFromTargetFollowers`,
`HandleAsync_UndoDeliveredToTarget_OtherFollowersUntouched`.

### F-1911-2: Create activities duplicated in the outbox (20x) — FIXED

**Repro:** alice-dev2 publishes a single `Create` (Note) to her outbox. The outbox then shows 20 copies
of the same `Create` activity (same IRI `https://iris-dev2.luit.ink/ap/v1/u/alice/creates/1911-1788205282`).

**Wire evidence:**
- `GET iris-dev2:8082/ap/v1/u/alice/outbox` → 20 items, all `type=Create`, all with the same `id`

**Root cause:** `AddToOutboxAsync` was not idempotent by activity IRI. The delivery worker's
at-least-once retry (or the replay-on-restart of the file-backed delivery queue) re-appended the
activity to the outbox each time it was processed.

**Fix:** `AddToOutboxAsync` in both `InMemoryActivityStore` and `FileBackedActivityStore` now checks
for an existing outbox entry with the same IRI before inserting. A re-recorded activity is a no-op.

**Tests:** `ActivityStore_Outbox_AddToOutbox_IsIdempotentByIri`.

### F-1911-3: Community follow delivery not completing

**Repro:** iris-dev1 community follows iris-dev2 community (signed POST → 202). Local `following` edge
recorded on dev1. After 30+ seconds, dev2's community `followers` is still empty.

**Wire evidence:**
- `GET iris-dev1:8081/ap/v1/c/iris/following` → includes `https://iris-dev2.luit.ink/ap/v1/c/iris`
- `GET iris-dev2:8082/ap/v1/c/iris/followers` → empty
- Delivery queue journal shows the Follow was enqueued (iris-a → iris-dev2 community inbox) but the
  in-memory channel drained without the edge appearing on the peer.

**Likely cause:** The delivery worker may be failing to deliver to the community inbox (signature
validation on the community inbox endpoint, or the community inbox handler not recording the edge).
Needs wire-level debugging (peer logs, signature verification trace).

## Environment notes

- `Iris__DumpKeyTo` added to iris-b in `docker-compose.yml` (mirrors iris-a) so the IrisSigner can
  sign as alice-dev2. This is a sample-only, opt-in env var (no secret committed).
- The delivery queue journal (`delivery-queue.jsonl`) is append-only and not truncated on delivery —
  it grows with every delivery. The in-memory channel is the source of truth for pending jobs; the
  journal is for replay-on-restart. This is by design (Phase 16.2) but makes the journal misleading
  for live inspection.

## Test counts

3 new integration tests (outbox dedup + Undo followers-edge). Existing suite: 1186 passing.
