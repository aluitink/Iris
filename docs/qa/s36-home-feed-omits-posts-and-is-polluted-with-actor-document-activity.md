# S36 — Home feed omits the user's own posts + followed posts and is polluted with actor-document activity (Update/Add/Remove/Follow/Undo/Delete/Like); UI renders "Your timeline is empty"

- **Class:** bug / data-integrity / regression — **Severity:** S2 (the home timeline is the primary surface; it shows essentially nothing)
- **Status:** open — **NEW regression on the current build (`27b1ba6`); not present in the 2026-09-20 fresh-rebuild re-tests**
- **Found:** Interop suite A2/A4 re-verify (Iris↔Iris), 2026-09-21, fresh QA cluster (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`), build `27b1ba6` (rebuilt 2026-09-21T03:04Z)
- **Related:** [S25](s25-remote-post-not-in-followers-home-feed.md) (remote post not in follower feed — **now subsumed** by this broader defect), [S18](s18-local-follow-timeline-empty.md) (local follow → empty timeline — **this is the local variant, now regressing again**), [S32](s32-delete-not-propagated-peer-stale-copy.md) (the `Delete`/tombstone noise seen in the feed is the S32 side-effect surfacing in the feed query)

## Symptom

On a **clean** cluster state (follows established, posts created, B's in-memory feed cache cleared by a service restart), the **home timeline (`/home`) renders empty** for the user even though they have posts and follow actors with posts:

1. **B's own post is not in B's own home feed.** As `ii-b1` (B), after posting `II-B-selftest local feed check` (Note `…/ii-b1/notes/06GC41BA17GFKF7SESSFYPAJXR`, confirmed in B's public outbox), `/home` shows **"Your timeline is empty. Follow people to see their posts here."** (0 console errors). The actor's **own** outbox post — which the feed code explicitly includes "regardless of follows" (`FeedService.BuildFeedUncachedAsync`, the `GetOutboxAsync(actorIri)` loop) — is absent.
2. **A local follow's post is not in the follower's feed.** `ii-a2` (A) follows `ii-a1` (A); `ii-a2` posts `II-A2-local local-follow feed check` (Note `…/ii-a2/notes/06GC4225PXY9KMRQRBEZXC8JSG`, in ii-a2's outbox). `ii-a1`'s `/home` does **not** show it (only an older boosted post renders).
3. **A remote follow's post is not in the follower's feed (S25).** `ii-a1` (A) posts `II-A4-2 reverify S25 …` (Note `…/ii-a1/notes/06GC40F0D2ZZNFF8704S1G6A9G`, `to`=Public, `cc`=followers); B's inbox log confirms `Inbox accepted: Create … Recipient: ii-b1`; B can `GET` the note (200). `ii-b1`'s `/home` is empty. The S25 fix (`GetDeliveredContentAsync`, present in the running `Iris.Server.dll` — `grep -c GetDeliveredContentAsync` = 3) does **not** make the post surface.

## Wire evidence (the feed the UI renders)

The UI's authenticated `GET /ap/v1/u/{me}/feed` (and `?source=people`) returns **HTTP 200** with **16–17 items** — but they are dominated by **actor-document activity noise**, and the real post `Create`s are **absent**. Captured via a page-context `fetch` hook on `ii-a1` (A) and `ii-b1` (B):

- `ii-a1` feed (16 items): `Follow(self)`, `Update`×several **on the actor IRI `u/ii-a1`** (actor-doc updates), `Undo`, `Delete` (note `…/06GC3AWSH…`), `Update` (note `…/06GC3AWSH…` "II-A9-1 edited"), **`Create` of `c/ii-a8-community` (a Group, the only content Create)**, `Announce` (boost of `…/06GC3AWSH…`), `Like`, `Remove`/`Add`/`Update` **on the actor IRI**, `Follow`×3. **No `Create` for ii-a2's post `…/06GC4225…`, no `Create` for ii-a1's own posts.**
- `ii-b1` feed (17 items, after a B restart to clear the in-memory cache): `Like`, `Follow(self)`, `Undo`, `Follow`(community), **`Create` of `c/ii-a8-community` (Group — the only content Create)**, `Follow`×2, `Update` on notes/actor, `Undo`, `Delete`, `Update`, `Like`, `Remove`/`Add`/`Update` on the actor IRI. **No `Create` for ii-b1's own post `…/06GC41BA…`, no `Create` for ii-a1's remote post `…/06GC40F0…`.**

So the server-side feed is returning a list that (a) includes a lot of **non-content activity** (actor `Update`/`Add`/`Remove`, self `Follow`, `Undo`, `Delete`, `Like`) and (b) **omits the content `Create`s** for the actor's own notes and for followed actors' notes. The UI renders only content (`Create`/`Announce`) items, and the only content item present is a **Group** (community join) plus an `Announce` whose object is "Content unavailable" (S32) — hence "timeline is empty".

## Why this is a regression (not the original S25)

- On the **2026-09-20 fresh-rebuild** re-tests, the feed surfaced followed content (the S25 symptom was specifically "only `Follow` activities, no `Create`" — i.e. the feed *did* return content-adjacent items and the defect was narrow). Now the feed returns **16–17 mixed items including actor-document activity** and **no post `Create`s at all** (own, local-follow, or remote-follow). The failure mode has changed and broadened.
- The **S25 fix is present in the running binary** and the delivered remote note **is in the object store** (`GET` 200), yet it does not surface — so the regression is *after* the delivered-content union, in either the feed's item selection/`GetFollowingAsync` resolution or the client render path.
- Restarting B (clearing the 30 s per-actor in-memory feed cache, `38f2bbc`) did **not** change the result → not a stale-cache artifact.

## Root cause (suspected — needs a dev code pass)

The home feed query (`FeedService.BuildFeedUncachedAsync`) is returning **actor-document-level activities** (the `Update`/`Add`/`Remove`/`Follow` whose object is the **actor IRI**, not a note) and **omitting the content `Create`s**. Two candidate defects, either or both:

1. **Feed source over-inclusive of actor-doc activity / under-inclusive of content.** The per-follow and own-outbox reads (`GetOutboxAsync`) appear to be returning **actor-document activity** (profile edits, follows, undo/delete, likes) rather than (or in addition to) the content `Create`s, and the content `Create`s are not in the returned set at all. The feed's de-dup/coalesce (`TruncateDedup`) and the visibility/thread filters then drop the non-content items on the client, leaving nothing.
2. **Client render filter drops everything except the (missing) content items.** The UI shows only `Create`/`Announce` content; since the server returns no post `Create`s, the timeline is empty. The server is the more likely primary defect (the feed contract should return content), but the client's silent drop of a 16-item feed into "empty" with no error is also worth a look.

The `Delete` + `Update` + `Remove`/`Add` on notes/actor IRIs in the feed is also the **S32** delete/tombstone noise leaking into the feed query.

## Re-verify (clean entry)

1. Fresh cluster; `ii-a1`↔`ii-b1` follow each other; `ii-a2` (A) follows `ii-a1` (A).
2. `ii-b1` (B) posts a Public note; `ii-a2` (A) posts a Public note; `ii-a1` (A) posts a Public note.
3. Each actor's `/home` must show: **their own post**, **the posts of actors they follow** (local + remote).
4. `GET /ap/v1/u/{me}/feed` must include the content `Create`s for those notes; it must **not** be dominated by actor-document `Update`/`Add`/`Remove`/`Follow`/`Undo`/`Delete`/`Like` activity.
5. UI renders the posts (not "Your timeline is empty"); 0 console errors.

## Re-verification evidence (2026-09-21, build `27b1ba6`)

- B own-post: `…/ii-b1/notes/06GC41BA17GFKF7SESSFYPAJXR` in B outbox; B `/home` = "timeline is empty"; feed (17 items) has **no `Create` for that note** (only `Create` = community Group). **Reproduces.**
- A local-follow: `ii-a2` → `ii-a1`; `…/ii-a2/notes/06GC4225PXY9KMRQRBEZXC8JSG` in ii-a2 outbox; ii-a1 `/home` does not show it; feed (16 items) has **no `Create` for it**. **Reproduces.**
- A→B remote (S25): `…/ii-a1/notes/06GC40F0D2ZZNFF8704S1G6A9G` delivered+accepted+stored on B; ii-b1 `/home` empty; feed has **no `Create` for it**. **Reproduces (S25 subsumed).**
- Feed cache cleared (B restart) → no change. 0 console errors on all three.

**S36 OPEN — broad home-feed regression on the current build; supersedes/blocks S25 (and re-opens the S18 local-timeline symptom).**

## Pass 129 (2026-09-21, current build `aebe420`) — S36 still reproduces live; captured the exact signed `/feed` body + outbox IRIs/types per dev's `162159b` request

Dev's `162159b` **isolated S36 and proved it non-reproducible in-process** (4 independent reproductions — FeedService in-memory, FeedService over EF/PostgreSQL, the real endpoint via WebAppFactory+TestServer signed owner `GET /u/{me}/feed?source=people` + unfiltered, and the `TruncateDedup` coalescing pass — **all PASS**: the owner's own note `Create` is returned, survives coalescing, and is served by the endpoint; the client `OutboxFilter.IsContentItem` is correct too). Conclusion: the server path (service → coalesce → endpoint → enrich → serialize) + client filter are **provably correct for the owner's own content in a fresh state**; the **live drop is data/environment-specific** (the live actors have accumulated S25/S32 noise the fresh reproductions don't have). **No code change** (no defect found in-process). Dev added 4 green regression-net tests and handed S36 back to QA for a two-instance re-verify **with a request to capture the exact signed `/feed` body + outbox IRIs/types if it still reproduces.**

**S36 STILL REPRODUCES on the live two-instance cluster (build `aebe420`):** `ii-a1` (A) `/home` renders only a **boost wrapper** ("Content unavailable — view original post", boosted by ii-b1, Boost=1) — **no own content posts** (the "timeline is empty" content-omission facet). This is the live data/environment state dev's fresh reproductions don't capture.

**Captured signed `GET /ap/v1/u/ii-a1/feed?source=people` (the exact body dev requested) → HTTP 200, 20 items, type breakdown:**

| type | count | notes |
|---|---|---|
| `Update`+`Activity` | **6** | **with FULL note content embedded** (e.g. `06GC5422N6…` → `II-A7-3 … [edited: S31 …]`, `06GC4N1YQ5…`, `06GC3VHEV6…` `S31 re-verify v2 edited`, `06GC3TCW00…` `S31 re-verify base text v2 (edited)`, `06GC3FH9RA…` `II-A9-1 edited cross-instance`, `06GC3DFXD3…` empty) — these are the owner's content posts **surfacing as `Update` (from edits), NOT `Create`** |
| `Like` | 4 | on own + remote notes |
| `Delete` | 3 | note tombstones (S32 noise) |
| `Follow` | 2 | self + remote |
| `Undo` | 1 | |
| **`Create`+`Activity`** | **1** | `creates/06GC3EAP04…` — a **community Group** (the only content `Create`; **not a content post**) |
| `Announce` | 1 | `ii-b1/announces/06GC3DYA5A…` → `06GC3AWSH…` (the **boost wrapper** that IS rendered) |
| `Remove`+`Activity` | 1 | actor-doc |
| `Add`+`Activity` | 1 | actor-doc |

**Key finding for dev:** the owner's content posts **ARE in the signed feed, but as `Update` activities (full content embedded), not `Create`** — because these notes were created-then-edited during the earlier re-verify passes (S31/S32 edits). The feed is dominated by `Update`/`Like`/`Delete`/`Follow`/`Undo`/`Remove`/`Add` (19 of 20 non-`Create`/`Announce`), with the **only `Create` being a community Group** and the **only `Announce` being the boost wrapper**. The UI renders only the `Announce` (boost) + (content) `Create`s; the content-bearing `Update`s are **not rendered as timeline items** (the client `OutboxFilter.IsContentItem` evidently doesn't treat an `Update`-with-embedded-content as renderable content). **So in a state where the owner's posts have been edited, the home feed surfaces the boost wrapper but omits the posts themselves** — the S36 content-omission, reproduced in the live data state. **This is the data/environment-specific shape (edited posts → `Update`, plus accumulated S32 `Delete`/`Undo` noise) that dev's fresh in-process reproductions (fresh `Create`, no edits, no noise) do not have.**

**Status: OPEN (S36) — reproduced live; dev's in-process proof of correctness holds for fresh state, but the live edited+noisy state still drops content. Suggested dev follow-up:** (a) confirm whether an `Update` activity whose embedded object is a Note **should** render as a timeline item (it carries the current content) — if yes, the client `IsContentItem` / server feed coalescing must surface `Update`(note) as content; (b) the feed should **coalesce** an `Update`(note) with the original `Create`(note) so an edited post appears **once** (current content) rather than as a non-renderable `Update` + an absent `Create`; (c) keep filtering the actor-doc `Update`/`Add`/`Remove` + `Undo`/`Delete`/`Follow`/`Like` noise out of the home timeline.

## Re-test (Pass 154, 2026-09-21, build `38ae87c`) — S36 still reproduces; A→B post delivery-to-cache also fails (proxy-only)

- **S36 (home feed) STILL REPRODUCES on `38ae87c`.** `GET A /ap/v1/u/ii-a1/feed` (authenticated) → HTTP 200, `OrderedCollection`, `totalItems`=35; first page (20 items) type breakdown: **7 `Update`(Note, with content) + 4 `Follow` + 4 `Delete` + 3 `Like` + 1 `Undo` + 1 `Create`(Group)**. **No II-A4-5 / II-A4-6 content posts present** (`hasII_A4_5`=false, `hasII_A4_6`=false). The feed is dominated by non-content activities; the owner's content posts surface as `Update`(note) (from edits), not `Create` — the same data/environment-specific shape as Pass 129. **S36 OPEN (unchanged).**
- **New data point — A→B post delivery-to-cache fails (proxy-only):** a fresh A post (II-S37-6, `…/ii-a1/notes/06GC6BCF…`) is in A's outbox (Create, to Public) but:
  - B's **note view** (`/note?iri=…A note…`) → **404** (not cached on B) after ~30s (no delivery lag).
  - B **home feed** (`totalItems`=21) does **not** contain it (no `06GC6BCF`, no `II-S37-6`).
  - B's **proxy** (`GET B /ap/v1/proxy/<A note IRI>`) → **200** (fetches the A note live from A).
  - So the A→B post is **not delivered into B's cache** (the proxy can fetch it on demand, but the home-feed delivery/cache path didn't store it) — a concrete A→B instance of the S36 feed-surfacing/delivery gap (the post is reachable via proxy but never lands in the follower's timeline/cache). This is the A→B mirror of the A4-5 facet (Pass 152) and confirms the S36 root cause is bidirectional.

**Status: S36 OPEN (S2, top priority) — re-confirmed on `38ae87c`; the A→B post delivery-to-cache failure (proxy 200, note view 404, not in home feed) is a new concrete data point supporting the S36 root cause. Awaiting dev's decision on the `Update`(note)-as-content / coalesce facet + the A→B delivery-to-cache facet.**

> **Cross-link (Pass 157):** [S39](s39-a-side-notifications-missing-b-side-receives-asymmetric-inbound-delivery.md) is the **notification-side mirror** of this finding's A→B delivery-to-cache gap — the same directional inbound-delivery root cause. On `38ae87c`, B's outbound activities (reply/Like/Follow) do **not** land in A's store/inbox, so `ii-a1` gets **0** notifications while `ii-b1` receives 22 from A's interactions. The A→B leg (S36: A post not cached on B) and the B→A leg (S39: B activity not stored in A's inbox) are two faces of the same **bidirectional delivery gap**. A dev fix to the shared-inbox / inbound delivery path is expected to address both.

## Re-test (Pass 169, 2026-09-21, build `38ae87c`) — S36 reproduces with a CLEAN content `Create` in the outbox (strongest evidence yet)

Dev's in-process code pass (commit `0ec58d3`) proved the server (`FeedService` → coalesce → endpoint) + client `OutboxFilter.IsContentItem` are **provably correct for a fresh owner content `Create`** and asked QA to re-verify from a clean entry + capture the exact signed `/feed` body + the actor's outbox IRIs/types if it still reproduces. **It still reproduces — and this pass captured the cleanest data shape yet, on `ii-a2`:**

- **`ii-a2` outbox** (`GET A /ap/v1/u/ii-a2/outbox`, totalItems=15) **HAS 2 content `Create`s** with `type: Note`:
  - `…/ii-a2/creates/…` → note `06GC754Q…` = **II-S31-6** (Pass 163 post, then **edited** → so it *also* has an `Update`)
  - `…/ii-a2/creates/…` → note `06GC6MBJ…` = **II-S34-notify** (Pass 157 local reply, **never edited** → a **clean content `Create`, no corresponding `Update`**)
  - Outbox page-1 type histogram: `Create` 2, `Like` 3, `Follow` 3, `Announce` 3, `Delete` 2, `Undo` 1, `Update` 1.
- **`ii-a2` signed home feed** (`GET A /ap/v1/u/ii-a2/feed?source=people` — the exact request the UI issued, **HTTP 200**, `totalItems`=48) **omits BOTH content `Create`s**. Its first page is dominated by non-content activities: `Delete` 2, `Update`(note) 1 (to `06GC754Q`), `Like` 3, `Follow` 4, `Undo` 1, `Announce` 1 (the `ii-b1 → 06GC3AWSH` boost wrapper), `Remove`/`Add` (actor-doc) 2. **No `Create`+`Activity` for a content Note appears on page 1** — the `II-S34-notify` clean `Create` (`06GC6MBJ`) and the `II-S31-6` `Create` (`06GC754Q`) are absent from the feed even though they are in the outbox.
- **Why this is the strongest evidence:** unlike the Pass 129/154 captures (where the owner's posts had all been *edited*, so they surfaced only as `Update`(note) and dev argued that is the data-specific shape), the `II-S34-notify` note (`06GC6MBJ`) was **never edited** — it exists **only as a content `Create`** in the outbox, yet it is **still absent from the home feed**. So the omission is **not** fully explained by the "edited → `Update`" shape; a **fresh, unedited content `Create` is dropped from the home feed on live data**, even though dev's in-process reproduction returns exactly that. The live actor has **accumulated S25/S32 noise** (the 2 `Delete`s, the `Undo`, the `Follow`/`Like`/actor-doc `Update`/`Add`/`Remove`), which the fresh in-memory/EF reproductions do not have — consistent with dev's "data/environment-specific" hypothesis, but it means the **live feed is still broken for a clean post**.
- **Follow-up data point (follow graph):** `ii-a2`'s own home feed is polluted with **ii-a1's** actor-doc + content activities (`ii-a1` `Update`(actor), `Add`/`Remove`, `Update`(note) to `06GC63QM`, etc.) because ii-a2 follows ii-a1; and ii-a1's own home feed (observed across Passes 129–168) renders **only the boost wrapper** (target `06GC3AWSH` = Tombstone) with **no own content** — the two facets together mean **neither local account gets its own posts in the home timeline** on `38ae87c`.

**Status: S36 OPEN (S2, top priority) — re-confirmed on `38ae87c` with a clean, unedited content `Create` (`06GC6MBJ`) present in the outbox but absent from the signed home feed (totalItems=48), plus the prior edited→`Update` shape. The exact signed `/feed` body + outbox IRIs/types are captured here for dev to replay the specific live data shape. Awaiting dev's decision: (a) why a clean owner content `Create` is dropped from the live home feed (the in-process repro returns it); (b) the `Update`(note)-as-content / coalesce facet; (c) filtering the actor-doc + S25/S32 noise (Delete/Undo/Follow/Like/Add/Remove) out of the home timeline.**

## Re-verification (Pass 186, 2026-09-21, build `2229b0ab`) — strongest repro yet: fresh own post in outbox but NOT in own feed; A→B delivery gap re-confirmed

- **Fresh ii-a1 post (never edited):** `II-S36-P186` (note `06GC9HM77347MWBBFZFGGB9HBR`, posted 16:22Z, `to`=Public, `cc`=followers). The note is **present in ii-a1's outbox** as a clean `Create` (9 Create items on page 1, this is the most recent).
- **ii-a1's own feed OMITS the fresh post:** `GET A /ap/v1/u/ii-a1/feed?source=people` → HTTP 200, `totalItems`=46, page-1 type histogram: `Update+Activity` 8, `Follow` 4, `Delete` 3, `Remove+Activity` 2, `Add+Activity` 2, `Like` 1. **Zero `Create` items.** The fresh `II-S36-P186` `Create` is **in the outbox but absent from the feed** — the strongest repro yet (a fresh, unedited, never-boosted, never-deleted own post is dropped from the owner's own home feed).
- **B-side: A→B delivery-to-cache gap re-confirmed:** `GET B /ap/v1/u/ii-a1/notes/06GC9HM77347MWBBFZFGGB9HBR` → **404** (empty body). The fresh A post is **not cached on B**. B's home feed (`GET B /ap/v1/u/ii-b1/feed?source=people` → 200, `totalItems`=32) = 0 `Create` items (6 Like, 9 Follow, 5 Undo — all noise). B `/home` UI = "Your timeline is empty."
- **S32 B-cache 404 data point:** `GET B /ap/v1/u/ii-a1/notes/06GC9DE5VSXHEWVTWYQ3311D0M` (the II-S39-P184 note, created Pass 184) → **404 on B** (empty body). The B-side cached copy of an A note is 404 (not a stale copy — no copy at all). This is the same A→B delivery-to-cache gap as S36.
- **Verdict:** S36 **STILL OPEN (S2, top priority)** on `2229b0ab`. The fresh own-post-not-in-own-feed repro (Pass 186) is the strongest evidence yet: a clean `Create` in the outbox is omitted from the feed query. The A→B delivery-to-cache gap (B 404s on A notes) persists. Dev's in-process reproductions (4 independent PASS) do not replicate this live data shape. **Handed back to dev with the II-S36-P186 evidence + the B-side 404s.**

## Re-verification (Pass 187, 2026-09-21, build `8243361c`) — S36 persists after cluster rebuild; 23rd consecutive confirmation

- **Cluster rebuilt to `8243361c`** (the S39 local-reply dial-base fix; first redeploy since `2229b0ab`).
- **Fresh ii-a1 post `II-S39-P187`** (note `06GC9KGVPK2X3QEY5QWMM867HR`, posted 16:30Z) is in the outbox as a clean `Create` (9 total Creates on page 1).
- **ii-a1's own feed STILL omits all own posts:** `GET A /ap/v1/u/ii-a1/feed?refresh=true` → HTTP 200, `totalItems`=46, page-1 type histogram: `Delete` 1, `Remove+Activity` 1, `Update+Activity` 3, `Add+Activity` 2, `Follow` 3, `Like` 1, `Update`(note) 2, `Undo` 1, `Announce` 1. **Zero `Create` items.** Both `II-S36-P186` and the fresh `II-S39-P187` are in the outbox but absent from the feed.
- **B-side: A→B delivery-to-cache gap persists:** B's feed (`GET B /ap/v1/u/ii-b1/feed`) → 200, `totalItems`=20, **0 Create items**. Neither the S39-P187 nor the S36-P186 A notes appear in B's feed. (Direct B-side note fetch is CORS-blocked from the browser; server-side curl confirms the note is 200 on A.)
- **UI `/home`:** renders only "Content unavailable — view original post" (a stale boost-wrapper for a tombstoned note). No own posts, no followed posts.
- **S24 D2 (foreign outbox) — bidirectional, stable:** A outbox: 28 creates / 8 foreign (3 pages). B outbox: 34 creates / 24 foreign (3 pages).
- **S39 CLOSED holding:** local-reply notification confirmed working on the new build (ii-a2 reply → ii-a1 "replied to your post" notification, just now).
- **Verdict:** S36 **STILL OPEN (S2, top priority)** on `8243361c`. The S39 dial-base fix did not affect the feed path (expected — different code path). The home-feed defect is unchanged: own content `Create`s are in the outbox but omitted from the feed query. **23rd consecutive pass confirming S36. Awaiting dev code pass on the feed query.**

## Re-verification (Pass 189, 2026-09-21, build `8243361c`) — S36 B-side confirmation: fresh B post also absent from B's own feed (25th consecutive)

- **Fresh ii-b1 post `II-S36-P189`** (posted 16:52Z, B-side). The note is in ii-b1's B outbox as a clean `Create`.
- **ii-b1's own B feed OMITS the fresh post:** `GET B /ap/v1/u/ii-b1/feed` → HTTP 200, `totalItems`=20, page-1 type histogram: `Like`, `Follow`, `Undo` (all noise). **Zero `Create` items.** The fresh B post is **in the outbox but absent from the feed** — the S36 defect is **not A-specific**; it reproduces identically on B for a B-local post.
- **B `/home` UI:** "Your timeline is empty. Follow people to see their posts here." (despite ii-b1 following ii-a1 and having their own posts).
- **Significance:** this is the first explicit **B-side own-post** confirmation. Prior passes focused on A-side (ii-a1) + B-side remote (A posts not in B feed). Now: **B's own posts are also omitted from B's own feed** — the feed-query defect is symmetric across both instances.
- **Verdict:** S36 **STILL OPEN (S2, top priority)**. 25th consecutive pass. The defect is confirmed on **both instances** (A: ii-a1 own posts omitted; B: ii-b1 own posts omitted) — not an A-side-specific or cross-instance delivery issue, but a **fundamental feed-query defect** that drops all content `Create`s from the home feed on both peers.
