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

### F-1911-1: Undo (unfollow) does not update the followed side's `followers` set

**Repro:** alice-dev2 follows alice-dev1 (edge on both sides). alice-dev2 publishes an `Undo` of the
Follow. After delivery: alice-dev2's `following` no longer lists alice-dev1 (correct), but
alice-dev1's `followers` still lists alice-dev2 (stale).

**Wire evidence:**
- `GET iris-dev1:8081/ap/v1/u/alice/followers` → still contains `https://iris-dev2.luit.ink/ap/v1/u/alice`
- `GET iris-dev2:8082/ap/v1/u/alice/following` → no longer contains `https://iris-dev1.luit.ink/ap/v1/u/alice`

**Likely cause:** The `UndoActivityHandler` on the unfollower's instance removes the local `following`
edge, but the `Undo` delivery to the followed side's inbox either (a) is not handled by a handler that
removes the `followers` edge, or (b) the `Undo` is delivered to the unfollower's own inbox (not the
followed side's inbox) — the delivery target is wrong.

### F-1911-2: Create activities duplicated in the outbox (20x)

**Repro:** alice-dev2 publishes a single `Create` (Note) to her outbox. The outbox then shows 20 copies
of the same `Create` activity (same IRI `https://iris-dev2.luit.ink/ap/v1/u/alice/creates/1911-1788205282`).

**Wire evidence:**
- `GET iris-dev2:8082/ap/v1/u/alice/outbox` → 20 items, all `type=Create`, all with the same `id`

**Likely cause:** The outbox append is not idempotent by activity IRI — the delivery worker's
at-least-once retry (or the replay-on-restart of the file-backed delivery queue) re-appends the
activity to the outbox each time it is processed. The outbox should dedupe by activity IRI.

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

No code changes; no new tests. Existing suite: 1183 passing.
