# S37 — Remote Like is stored + rendered, but the note's `likedCount` / inline Like-count is not materialized

- **Severity:** S3 (low)
- **Status:** **PARTIALLY FIXED (Pass 141, build `38ae87c`)** — the **wire-level `likedCount` (Like/unlike path) is FIXED**: dev's `38ae87c` ("S37: refresh Note likedCount immediately on remote Like/unlike") adds `ObjectInteractionCountRefreshService.RefreshObjectCountsAsync(objectIri, ct)`, called from `LikeActivityHandler` (after `RecordLikeAsync`) + `UndoActivityHandler` (after `RemoveLikeAsync`), so the Note's denormalized `likedCount`/`score`/`sharedCount`/`repliedCount`/`dislikedCount` (under the `iris:` namespace) are re-computed + persisted **immediately** on a Like/unlike — verified live: a fresh A note Liked by B now carries `…/ns#likedCount: 1` + `…/ns#score: 1` on the very next read (no 30 s wait; before the fix these were **absent**). **Two residual facets remain OPEN:** (1) the object-detail **Like button UI still renders "0"** (the client's button count doesn't read the denormalized `likedCount` — a client/UI facet); (2) the **Boost `sharedCount` path is not covered** (dev wired Like/unlike only, no `AnnounceActivityHandler` change) — so S28's remaining count facet (Boost `sharedCount`/`shares.totalItems`) may still rely on the periodic pass. **Status: PARTIALLY FIXED — wire `likedCount` FIXED; Like-button UI + Boost `sharedCount` facets open.**
- **Test:** A2 (remote Like) — count-materialization facet
- **Component:** Server (like-count materialization on the Note object) — dev-owned; QA documents only.

## Summary

When a **remote Like** is applied to a Note, the like is correctly **delivered, processed, stored, and rendered** (the `/likes` collection `totalItems` increments and the object-detail UI shows "N like"), **but the Note's denormalized `likedCount` property and the inline Like-button count both remain `0`/`None`**. So a client reading the Note document (rather than fetching `/likes`) sees a like count of 0 even though the like exists. This is the **count-materialization analog of the S28 `shares`-count gap** (S28's Announce is dropped entirely; here the Like is stored but its count is not materialized).

## Reproduction (build `27b1ba6`, 2026-09-21)

Reproduced on **two** independent cross-instance Likes (A `ii-a1` → B `ii-b1` notes):

### Note 1 — `…/ii-b1/notes/06GC4CXP22T3Q8QSAPPB99HWC0`
| Check | Value |
|---|---|
| B log | `Inbox accepted: Like from ii-a1 targeting …/06GC4CXP22` + `LikeActivityHandler processed … — ok` ✓ |
| `GET <note>/likes` | `totalItems: 1` ✓ |
| B object-detail UI | **"1 like"** + **"Likes (1)"** tab ✓ |
| `GET <note>` `likedCount` | **`None`** ✗ |
| `GET <note>` embedded `likes.totalItems` | **`0`** ✗ |
| Inline Like-button count (A + B UI) | **`0`** ✗ |

### Note 2 — `…/ii-b1/notes/06GC41BA17GFKF7SESSFYPAJXR`
| Check | Value |
|---|---|
| B log | `Inbox accepted: Like from ii-a1 targeting …/06GC41BA17` + `LikeActivityHandler processed … — ok` ✓ |
| `GET <note>/likes` | `totalItems: 1` ✓ |
| `GET <note>` `likedCount` | **`None`** ✗ (re-checked after delay — stable, not a transient) |
| `GET <note>` embedded `likes.totalItems` | **`0`** ✗ |

**Reproducible:** both notes show the identical pattern — `/likes` collection correct, Note-level `likedCount`/embedded count not materialized.

## Expected

After a Like is applied to a Note, the Note's `likedCount` (and the embedded `likes.totalItems`) should reflect the like count, so a client reading the Note document sees the correct count.

## Actual

The like is stored and `/likes` is correct, but the Note's `likedCount` stays `None` and the embedded `likes.totalItems` stays `0`. The UI's "N like" badge (which reads `/likes` or a separate count) is correct, but the inline Like-button count and the wire `likedCount` are not.

## Impact

- Clients that read the Note document's `likedCount` (the standard AS2 property) see 0 even when likes exist → **undercount for API consumers / other instances** fetching the note.
- The inline Like-button count is wrong (shows 0) until the user refetches `/likes`.
- Lower severity than S28 because the like **is stored and rendered**; only the denormalized count on the Note is stale.

## Relationship to other findings

- **S28** (remote Announce/Boost dropped; `shares` count empty): same *class* — a denormalized collection/count on the Note not reflecting applied interactions — but S28's interaction is **dropped entirely**, whereas S37's Like is **stored**; S37 is the count-only facet.
- **S36** (home feed omits posts): independent (feed assembly, not per-object counts).

## Suggested fix (dev)

When a remote Like is applied (in the `LikeActivityHandler` / the like-storage path), **update the target Note's `likedCount`** (denormalized counter) and/or ensure the Note document's embedded `likes.totalItems` is recomputed, mirroring whatever makes the `/likes` collection return the correct count.

## Verification (after fix)

1. A likes a B note.
2. `GET <note>` on B → `likedCount` == 1 (and embedded `likes.totalItems` == 1).
3. Inline Like-button count shows 1 (A + B UI).
4. Repeat for a second note to confirm it's systemic-fixed, not per-note.

## Log evidence

```
# B log (both notes):
Inbox accepted: Like from https://qa-iris-a.luit.ink/ap/v1/u/ii-a1 targeting https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/notes/06GC4CXP22T3Q8QSAPPB99HWC0. Recipient: .../ii-b1, Peer: qa-iris-a.luit.ink
Handler LikeActivityHandler processed Like ... (actor .../ii-a1, recipient .../ii-b1) — ok

# Wire (both notes):
GET <note>/likes -> totalItems: 1
GET <note>       -> likedCount: None, likes.totalItems: 0
```

## Re-test (Pass 117, 2026-09-21, current build) — broadened: LOCAL Like/Boost too

**The count-materialization gap is GENERAL — it is not limited to remote actions.** On note `06GC4RR4` (ii-a1's own note on A), `ii-a1` performed a **local Like** (and the note already had a **remote Boost** from ii-b1, S28). Afterward:

- **Wire (`GET A <note>`):** `likedCount` = **None**, `likes.totalItems` = **0**, `sharedCount` = **None** — the denormalized counts are **not materialized even for a local Like + the remote Boost**.
- **Collections (correct):** `GET A <note>/likes` → `totalItems` = **1** (the local Like); `GET A <note>/shares` → `totalItems` = **1** (the remote Boost).
- **UI (mixed):** the **Like button shows "1"** (pressed) — the UI derives the like count client-side — but the **Boost button count is still 0** (the boost count is NOT shown on the button) even though the note shows a **"1 boost"** line + a **"Shares (1)"** tab. So the UI partially materializes counts (Like button), but the Boost button count and all the **wire** counts lag.

**Verdict (current build): S37 broadened — the note's `likedCount`/`sharedCount`/embedded `likes.totalItems`/`shares.totalItems` are not materialized for BOTH local and remote Like/Boost; the `/likes` + `/shares` collections are correct, and the UI derives the Like count but not the Boost button count.** The fix is to **materialize the denormalized counts (`likedCount`/`sharedCount`) whenever a Like/Announce is recorded (local or remote)**, and reflect them in the object document + the Boost button. **Status: OPEN (broadened to local + remote).**

## Re-test (Pass 141, 2026-09-21, build `38ae87c` — dev's S37 fix deployed)

**Dev committed `38ae87c`: "S37: refresh Note likedCount immediately on remote Like/unlike."** The fix (the Pass-139 WIP, now committed): a new `ObjectInteractionCountRefreshService.RefreshObjectCountsAsync(objectIri, ct)` re-computes + persists the object's denormalized `likedCount`/`sharedCount`/`repliedCount`/`dislikedCount` (+ derived `iris:score`) **immediately** after a like edge is recorded/removed, so the object document is correct on the very next read instead of waiting for the periodic 30 s pass (which `WriteCountsIfChanged` only updates when the count *differs*, so an absent/stale-0 count was never refreshed — the Pass-140 finding). Called from `LikeActivityHandler` (after `RecordLikeAsync`) + `UndoActivityHandler` (after `RemoveLikeAsync`); the service is registered as a singleton (resolvable by the inbound handlers) **plus** a separate hosted-service instance for the periodic + startup pass. Rebuilt + redeployed the QA cluster to `38ae87c`.

**Wire-level `likedCount` — FIXED.** Posted a fresh A note **`II-S37-5`** (`…/u/ii-a1/notes/06GC5MR7VQSR9TC2V2Z4KXPNBW`), then B (ii-b1) **Liked** it. On A (owner), the note **document now carries the denormalized counts under the iris: namespace IMMEDIATELY** (no 30 s wait):

| Check | Value (build `38ae87c`) |
|---|---|
| A log | `Inbox accepted: Like from …/ii-b1 targeting …/06GC5MR7` + `LikeActivityHandler processed … — ok` ✓ |
| `GET A <note>/likes` | `totalItems: 1` ✓ |
| `GET A <note>` `…/ns#likedCount` | **`1`** ✓ (materialized on the Like itself — **before the fix absent**, Pass 140) |
| `GET A <note>` `…/ns#score` | **`1`** ✓ |
| `GET A <note>` `…/ns#sharedCount` / `repliedCount` / `dislikedCount` | `0` / `0` / `0` ✓ |
| A object-detail **"Likes (1)" tab** | ✓ (reflects the count) |
| `GET A <note>` embedded `likes.totalItems` | **`0`** ✗ (the doc serializer keeps the embedded collection count at 0) |
| A object-detail **Like button** count | **`0`** ✗ (the client's button count doesn't read `likedCount`) |

**Two residual facets remain OPEN:**

1. **Like-button UI (client/UI facet, S3, low):** the object-detail **Like button still renders "0"** even though the wire `likedCount`=1 + the "Likes (1)" tab are correct. The client's button count does not read the denormalized `likedCount` (it likely derives from the embedded `likes.totalItems`, which the doc serializer keeps at 0). The **wire count is FIXED**; the **inline button** is not.
2. **Boost `sharedCount` (S28 count facet) — ALSO FIXED (verified in Pass 141):** B then **Boosted** the same note → A note doc now shows `…/ns#sharedCount: 1` + `…/ns#likedCount: 1` + `/shares` totalItems=1 + `/likes` totalItems=1. So the **wire `sharedCount` materializes too** — `RefreshObjectCountsAsync` computes all four counters, and although dev's immediate-refresh is only wired into Like/Undo, the Boost `sharedCount` still converges (via the periodic pass). **S28's remaining wire count facet is now FIXED as well.**

**Verdict (build `38ae87c`): S37 + S28 wire counts FIXED — the Note's denormalized `likedCount` (Like/unlike, materialized **immediately**) and `sharedCount` (Boost) both materialize under the iris: namespace + the "Likes (1)"/"Shares (1)" tabs are correct. Single residual facet: the object-detail **Like/Boost button UI still renders "0"** (the client button count doesn't read the denormalized `likedCount`/`sharedCount`; the embedded `likes.totalItems`/`shares.totalItems` in the doc also stay 0) — a client/UI + doc-serializer facet (S3, low).** **Status: PARTIALLY FIXED — wire `likedCount` + `sharedCount` FIXED; Like/Boost-button UI facet open.**
