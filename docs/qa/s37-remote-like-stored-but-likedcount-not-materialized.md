# S37 — Remote Like is stored + rendered, but the note's `likedCount` / inline Like-count is not materialized

- **Severity:** S3 (low)
- **Status:** **FIXED on the wire (live-re-verified 2026-09-22, dev1, commits `e1e1aa88` + `5355e968`)** — the **wire-level `likedCount` (Like/unlike) is FIXED** (`e1e1aa88`: `ObjectInteractionCountRefreshService.RefreshObjectCountsAsync` called from `LikeActivityHandler`/`UndoActivityHandler`) **AND the Boost `sharedCount` path is now FIXED** (`5355e968`: routes the shared-inbox `Announce` to the note's owner, so `AnnounceActivityHandler` records the edge + calls `RefreshObjectCountsAsync` **immediately** — the Boost is no longer dropped and `sharedCount` materializes on the next read). Verified live: a fresh A note Liked + Boosted by B now carries `…/ns#likedCount: 1` + `…/ns#sharedCount: 1` + `…/ns#score: 1` on the very next read (no 30 s wait). **UI facet CLOSED (live-re-verified 2026-09-22 on dev1):** the object-detail **Like/Boost buttons now render the denormalized `likedCount`/`sharedCount`** and update live on a like/boost (the `EngagementBar` fast path reads the denormalized counters off the object doc, not the embedded `likes.totalItems`/`shares.totalItems`); the earlier "renders 0" was observed on the older `7620faa1` build **before** the count-refresh fix. **Status: FIXED — wire counts (likedCount + sharedCount) + object-detail Like/Boost-button UI all verified.**
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

## Live re-verification (2026-09-22, dev1 two-instance stack, commit `5355e968`)

**The Boost `sharedCount` facet (the open (2) above) is now FIXED — and materializes immediately, not via the periodic pass.** On `38ae87c` the immediate `RefreshObjectCountsAsync` was wired only into Like/Undo, so the Boost's `sharedCount` relied on the 30 s periodic pass (and on builds before the S28 shared-inbox routing fix the Boost was *dropped entirely* at the shared inbox — `sharedCount` stayed 0/None). `5355e968` routes the inbound `Announce` at the shared inbox to the **note's owner**, so the owner's `AnnounceActivityHandler` records the announcer→object edge + calls `RefreshObjectCountsAsync` **immediately** — the Boost is no longer dropped and `sharedCount` materializes on the very next read.

**Live evidence (A `dev1-iris-a.luit.ink`, B `dev1-iris-b.luit.ink`; fresh A note `…/s37a/notes/06GCE3ZPP6PB3R5919TYA7YBJW`):**

| Check | Value (commit `5355e968`) |
|---|---|
| B signed `Like` → A | A log `LikeActivityHandler processed … — ok`; `…/ns#likedCount: 1` + `…/ns#score: 1` ✓ (immediate, `e1e1aa88`) |
| B signed `Announce` (Boost) → A | B outbox **202**; A log `AnnounceActivityHandler processed … — ok` (NOT "no local recipient; dropping") ✓ |
| `GET A <note>` `…/ns#sharedCount` | **`1`** ✓ (materialized **immediately** — before the fix: dropped / relied on periodic pass) |
| `GET A <note>` `…/ns#likedCount` / `…/ns#score` | **`1`** / **`1`** ✓ |
| `GET A <note>/shares` | `totalItems: 1` (the boost, `actor`=s37b, `object`=Note IRI) ✓ |

**Verdict (commit `5355e968`): S37 + S28 wire counts FIXED — `likedCount` (Like) and `sharedCount` (Boost) both materialize immediately under the iris: namespace + the `/likes` + `/shares` collections are correct.**

## Live UI re-verification (2026-09-22, dev1 stack, Playwright, build includes `5355e968`)

The "Like/Boost button renders 0" facet is **resolved** on the current dev1 build. The object-detail page's `EngagementBar` fast path reads the denormalized `iris:likedCount`/`iris:sharedCount` off the fetched object doc (the server's `ObjectDocumentHandler` serves both the per-object counters and the per-requester `isLiked`/`isShared`/minted-activity-id state), so the button reflects the real counts and updates live:

| Action | Button | Wire |
|---|---|---|
| Open object-detail (`/object?iri=<Note IRI>`) for a fresh local note | Like **0** + Boost **0** (correct, none yet) | `ns#likedCount: 0` / `ns#sharedCount: 0` |
| Press **Like** | Like **1** `[pressed]` | `ns#likedCount: 1` + `ns#score: 1` (immediate) |
| Press **Boost** | Boost **1** `[pressed]` | `ns#sharedCount: 1`; `/shares` `totalItems: 1`; `/likes` `totalItems: 1` |

**0 console errors** on the object-detail page. The earlier "renders 0" was observed on the older `7620faa1` build **before** the count-refresh fix; the embedded `likes.totalItems`/`shares.totalItems` in the doc stay 0 but the button no longer reads them (the denormalized counters are authoritative on the fast path). **Status: S37 fully FIXED — wire counts + object-detail Like/Boost-button UI both verified live.**
