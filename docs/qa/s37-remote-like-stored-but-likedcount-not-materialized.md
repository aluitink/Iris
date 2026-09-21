# S37 — Remote Like is stored + rendered, but the note's `likedCount` / inline Like-count is not materialized

- **Severity:** S3 (low)
- **Status:** OPEN — **broadened (Pass 117, current build): the count-materialization gap is GENERAL — it affects LOCAL Like/Boost too, not just remote.** The wire `likedCount`/`sharedCount`/embedded `likes.totalItems`/`shares.totalItems` are not materialized even for a local action (see Pass 117). The UI derives some counts client-side (Like button shows the count), but the **wire counts** and the **Boost button count** lag.
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
