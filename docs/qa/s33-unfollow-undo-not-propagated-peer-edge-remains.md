# S33 — Unfollow (`Undo` of `Follow`) emitted locally but NOT propagated; peer's `followers` edge remains

- **Class:** bug / federation — **Severity:** S2
- **Status:** open
- **Found:** Interop suite A10 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S32](s32-delete-not-propagated-peer-stale-copy.md) (same "bare-IRI Undo/Delete → peer can't resolve recipient → not applied" class). Distinct from [S24](s24-cross-instance-follow-state-inconsistent.md) (follow *state*; this is the unfollow *propagation*).

## Symptom

`ii-b1` (B) followed `ii-a1` (A) (A2). A's `ii-a1/followers` = `[ii-b1@B]` (count 1). `ii-b1` pressed **Unfollow** on `ii-a1`'s actor page.

**Local (B) unfollow is correct:**
- `GET B /ap/v1/u/ii-b1/following` → **count 0** (no longer follows ii-a1) ✓
- `GET B /ap/v1/u/ii-b1/outbox` → `Undo` activity, `id` = `…/ii-b1/undos/06GC0T1KAK40RBSEB6TS01VQ5W`, `object` = **`…/ii-b1/follows/06GC0G2GXSP0H524BRG9D8MBZ4`** (the original Follow IRI) — **but the `object` is a bare IRI string, not an embedded `Follow` activity** (so it carries no `actor`, no `to`/`cc`, no followee reference).

**Peer (A) edge remains:**
- `GET A /ap/v1/u/ii-a1/followers` → **still `[ii-b1@B]` (count 1)** — the unfollow did **not** propagate; A still lists ii-b1 as a follower.
- A `qa-iris-a` log: `Inbox rejected: unknown recipient https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/follows/06GC0G2GXSP0H524BRG9D8MBZ4` — the `Undo` was **rejected** because its `object` is a bare `follows/` IRI that A cannot resolve to a local recipient (it is a foreign activity IRI, not an embedded activity with `actor`/`to`).

So the follow edge is removed on B but **not on A** — A's `followers` collection is stale (still contains the unfollower).

## Root cause (suspected)

The `Undo` activity's `object` is serialized as a **bare IRI** to the original `Follow` instead of the **full embedded `Follow` activity** (with `actor` = ii-b1, `object` = ii-a1, and `to`/`cc` addressed to ii-a1). Consequences:
1. The `Undo` is not properly **addressed** (no `to`/`cc`), so it isn't reliably delivered to the followee's inbox.
2. Even if delivered, the peer **rejects** it (`unknown recipient <follows-IRI>`) because a bare foreign IRI can't be resolved to a local recipient — the embedded activity's `actor`/`object` is what the inbox needs to find the local edge to remove.

Compare S32 (Delete `to`/`cc` empty + note-IRI recipient rejected). No `file:line` yet — needs a code pass on the `Undo` construction (embed the full `Follow` with `actor`/`to`?) and peer inbox recipient resolution for `Undo` objects.

## Fix (agreed approach)

- An `Undo` of a `Follow` must carry the **full embedded `Follow` activity** (`actor` = unfollower, `object` = followee, `to`/`cc` addressed to the followee), not a bare IRI, so it is delivered to and resolvable by the followee's instance.
- The peer inbox must resolve the `Undo` to the local follow edge and **remove** it (the followee's `followers` collection must drop the unfollower).

## Re-verify (clean entry)

1. `ii-b1` (B) follows `ii-a1` (A); A `ii-a1/followers` = `[ii-b1]`.
2. `ii-b1` presses Unfollow.
3. B `following` = empty; B outbox `Undo` has an **embedded** `Follow` (actor `ii-b1`, object `ii-a1`, addressed to `ii-a1`).
4. A log: the `Undo` is **received and applied** (no "unknown recipient" rejection).
5. `GET A /ap/v1/u/ii-a1/followers` → **empty** (ii-b1 removed). ← the fix

**Re-verification evidence (Interop A10, 2026-09-20, QA stack):** B `following` = 0; B outbox `Undo` object = bare IRI `…/ii-b1/follows/06GC0G2GXSP0H524BRG9D8MBZ4` (no embedded Follow); A `ii-a1/followers` still = `[ii-b1]` (count 1); A log = `Inbox rejected: unknown recipient …/ii-b1/follows/06GC0G2GXSP0H524BRG9D8MBZ4`. **S33 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CONFIRMED — reproduces (wire), mirror direction.** This run `ii-a1` (A) unfollowed `ii-b1` (B):
- **Local (A):** A outbox (inline) has `Undo` `…/ii-a1/undos/06GC1HP0SYNTYN1M1NC9BZ4DYM`, `object` = **bare IRI** `…/ii-a1/follows/06GC1APX76ZGTS4B9P9DRRA1J4` (the original Follow IRI; not an embedded Follow).
- **Peer (B):** `qa-iris-b` log = `Inbox rejected: unknown recipient https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/follows/06GC1APX76ZGTS4B9P9DRRA1J4`; `GET B /ap/v1/u/ii-b1/followers` → **still `[ii-a1]` (count 1)** — the edge was **not** removed on the peer.

Same bare-IRI `Undo` + peer "unknown recipient" rejection + stale peer `followers` as the original (A→B direction). Restored afterward: `ii-a1` re-followed `ii-b1` (new Follow `…/ii-a1/follows/06GC1HTBP45RBES6T46H8FM65C`, accepted on B), edge back on both sides.

S33 OPEN (reproduces on a fresh build).
