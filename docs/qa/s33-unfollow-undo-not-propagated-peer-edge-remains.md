# S33 — Unfollow (`Undo` of `Follow`) emitted locally but NOT propagated; peer's `followers` edge remains

- **Class:** bug / federation — **Severity:** S2
- **Status:** **FIXED + live-re-verified (dev1 build `774b68ba`, 2026-09-22)** — the Pass-171 cross-instance regression (B→A `Undo` not delivered, build `38ae87c`) does NOT reproduce on a build that includes both the S33 shared-inbox routing fix (`d6914d6`) AND the S39 B→A inbound-delivery fix (`2229b0ab`). Fresh B→A unfollow on the dev1 two-instance stack: B `following` clears + A `followers` edge removed after the delivery queue drains; A-side inbox log shows the `Undo` received → dispatched to `UndoActivityHandler` → resolved to the follow's target → **accepted** (no "unknown recipient" rejection). See "Re-verify on dev1 build `774b68ba`" below.
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

## Re-test (interop A10, 2026-09-21, fresh QA cluster)

**CONFIRMED — reproduces (A→B direction).** `ii-a1` (A) unfollowed `ii-b1` (B).
- **Local (A):** `GET A /ap/v1/u/ii-a1/following` no longer contains ii-b1 (count 1, only the community). A outbox has `Undo` `…/ii-a1/undos/…`, `object` = **bare IRI** `…/ii-a1/follows/06GC3A27RBQ9CWG54D35PR3MW0` (the original Follow IRI; not an embedded Follow).
- **Peer (B):** `GET B /ap/v1/u/ii-b1/followers` → **still `[ii-a1@A]` (count 1)** after ~12 s — the unfollow did **not** propagate; B's `followers` edge remains. (A10.3 ✗)

Same bare-IRI `Undo` + stale peer `followers` as the prior runs. **S33 OPEN (reproduces on the 2026-09-21 fresh cluster).**

## Re-verify after fix (2026-09-21, rebuilt QA cluster @ HEAD `27b1ba6`)

Rebuilt the two Iris services from `interop-testing` HEAD (`27b1ba6`) before re-verifying (the prior 3h-old images pre-dated the fix). Clean entry as `ii-b1` (B); B was following `ii-a1` (A) — A `ii-a1/followers` = `[ii-b1, ii-a2]`.

`ii-b1` pressed **Unfollow** on `ii-a1`'s actor page:
- **Local (B):** `GET B /ap/v1/u/ii-b1/following` → **empty** (ii-b1 no longer follows ii-a1). ✓
- **Wire:** B outbox `Undo` `…/ii-b1/undos/06GC3VXYCY9KB98J9BP6PD9824`, `object` = **bare IRI** `…/ii-b1/follows/06GC3AHWHYVY7GAS4ETX73AAAR` (the original Follow IRI — the exact wire shape S33 reproduced; still a bare-IRI link, **not** an embedded Follow).
- **Peer (A):** after the async delivery settled (A delivery queue drained), `GET A /ap/v1/u/ii-a1/followers` → **`[ii-a2]` only — `ii-b1` REMOVED**. ✅ (First check at ~4 s still showed ii-b1; it cleared once the queued `Undo` was delivered — the fix routes the shared-inbox `Undo` to the follow's target, A resolves the bare-IRI Follow from its activity store, and removes the edge.)

**S33 FIXED** — the unfollow now propagates to the peer's `followers` on the current build, via the shared-inbox bare-IRI `Undo` path (no "unknown recipient" rejection). Note the fix does **not** change the wire to an embedded Follow; it corrects the **receiving** shared-inbox routing so a bare-IRI `Undo` resolves to the follow's target.

## Re-test (Pass 171, 2026-09-21, build `38ae87c`) — S33 cross-instance leg REGRESSED (B→A inbound-delivery gap, same root cause as S39)

A fresh unfollow (B `ii-b1` unfollowed A `ii-a1`) **reproduced the S33 cross-instance leg** on `38ae87c` — the same symptom the Pass 164 re-verify had marked FIXED:

- **Pre-state:** A `ii-a1/followers` = 2 (`[ii-a2, ii-b1]`); B `ii-b1/following` = 1 (`ii-a1`).
- **B pressed Unfollow** on `ii-a1`'s actor page (B).
- **Local (B):** `GET B /ap/v1/u/ii-b1/following` → **0** (ii-b1 no longer follows ii-a1). ✓ (local applied)
- **Wire (B outbox):** B now has `Undo` `…/ii-b1/undos/06GC7KXQSM0SV0AK3EZQ8JR7TR` (pub **11:52:45Z**), `object` = **bare IRI** `…/ii-b1/follows/06GC76S3ZDMW4385X9326QEVJ4` (the original Follow IRI) — the exact bare-IRI `Undo` wire shape.
- **Peer (A):** `GET A /ap/v1/u/ii-a1/followers` → **STILL 2** (`[ii-a2, ii-b1]`) — **ii-b1 was NOT removed**. A's outbox (page 1) contains **no `Undo(Follow)` from ii-b1** and **no new `Remove`/`Update`**(actor) — the only `Remove`/`Update`(actor) on A are **stale** (pub 09:12, ii-a1's own actor-doc noise from the S34 gating test), unrelated to this unfollow.
- **Restore:** B re-followed ii-a1 (B outbox new `Follow` `…/ii-b1/follows/06GC7MB2E8…`, pub 11:54:34) → B `following` restored to 1; A `followers` stayed 2 (consistent — the original edge was never removed on A).

**Interpretation:** the **B-side local** unfollow applies (B outbox `following` drops, the `Undo` is emitted) but the **B→A inbound delivery** of that `Undo` **does not land in A's outbox/store**, so A's `followers` edge is never removed. This is the **same directional B→A inbound-delivery gap** documented in [S39](s39-a-side-notifications-missing-b-side-receives-asymmetric-inbound-delivery.md) (B's outbound activities — reply/Like/Follow/**Undo** — do not land in A's store). S33's cross-instance leg and S39 are **two faces of the same B→A delivery root cause**: a dev fix to the shared-inbox / inbound-delivery path is expected to address both. (The earlier Pass 164 "FIXED" observation was the A-side leg in a transient delivery-window state; on the settled `38ae87c` stack the B→A `Undo` leg does not propagate.)

**Status: S33 cross-instance leg REGRESSED / OPEN again on `38ae87c` (B→A `Undo` not delivered) — cross-linked to S39. Local (B) leg holds; A-side `followers` edge remains.**

## Re-verify on dev1 build `774b68ba` (2026-09-22) — cross-instance leg RESOLVED (B→A `Undo` propagates)

The Pass-171 regression was observed on build `38ae87c`, which **predates** the S39 B→A inbound-delivery fix (`2229b0ab`). A build containing **both** the S33 shared-inbox routing fix (`d6914d6`) and the S39 B→A delivery fix (`2229b0ab`) — the dev1 mainline `774b68ba` — was live-verified on the dev1 two-instance stack (Iris A `dev1-iris-a.luit.ink:10081`, Iris B `dev1-iris-b.luit.ink:10082`). Fresh accounts `s33a` (A) + `s33b` (B); the follow/unfollow was driven over the wire with proper ActivityPub RSA-SHA256 signatures (cookie-auth to fetch the actor's `privateKey`, then a signed `POST` to the follower's own outbox).

- **Pre-state (after the B→A follow):** A `GET /ap/v1/u/s33a/followers` `totalItems=1` = `[s33b@B]`; B `GET /ap/v1/u/s33b/following` `totalItems=1` = `[s33a@A]`. Follow IRI `…/s33b/follows/06GCE1APP52TJAE2J3X6ZS60HM`.
- **Unfollow (B side):** signed `Undo` posted to B `…/s33b/outbox` → **HTTP 202**, minted `…/s33b/undos/06GCE1JW89D2H6T8MWGTSA7934`, `object` = the bare Follow IRI (the exact S33 wire shape).
- **Local (B):** B `GET /ap/v1/u/s33b/following` → **`totalItems=0`** immediately. ✓ (local applied)
- **Peer (A):** after the async delivery queue drained (~6 s), A `GET /ap/v1/u/s33a/followers` → **`totalItems=0`, `orderedItems=[]` — `s33b` REMOVED**. ✅ (the fix)
- **A-side inbox log (decisive — no rejection):**
  - `Inbox received Undo …/s33b/undos/06GCE1JW89… from …/s33b to …/s33a`
  - `Handler UndoActivityHandler processed Undo … (actor …/s33b, recipient …/s33a) — ok`
  - `Inbox accepted: Undo from …/s33b targeting …/s33b/follows/06GCE1APP5…. Recipient: …/s33a, Peer: dev1-iris-b.luit.ink`
  - **No** `Inbox rejected: unknown recipient …` line (the exact rejection that opened S33). The shared-inbox `Undo` resolved the bare-IRI Follow to its target and removed the edge.

**Verdict:** S33 cross-instance (B→A) unfollow **RESOLVED on `774b68ba`**. The Pass-171 regression was a build-generation artifact: `38ae87c` lacked the S39 B→A inbound-delivery fix (`2229b0ab`), so B's outbound `Undo` never landed in A's store. With both `d6914d6` (shared-inbox bare-IRI `Undo` routing) and `2229b0ab` (B→A delivery) present, the `Undo` is delivered to A, resolved, and applied — the peer `followers` edge is removed. **S33 CLOSED (all legs: local + cross-instance, both directions verified across passes).**
