# S24 — Cross-instance follow: profile "Following" tab omits remote actors, spurious self-follow in outbox, remote-actor direct GET 404s

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** **S24 CLOSED** — **D1 (Following-tab omits remote actor) FIXED** (Pass 116). **D2 (foreign activities in the local actor's outbox) FIXED** (2026-09-22) — three complementary fixes: (a) `GetOutboxAsync` in all three store implementations (`EfActivityStore`, `InMemoryActivityStore`, `FileBackedActivityStore`) now filters the outbox read path to exclude **foreign Follow activities** (a remote actor's follow-request landing in the followed actor's outbox, plus the spurious self-`Follow`) — the repair `0d05342e` scoped dev2's original `activity.Actor` filter to `Follow`-only, because filtering *all* items broke the F-15 community fan-out (a remote actor's Create/Announce/Like recorded in a local member's outbox); (b) `FeedService` own-outbox branch (`61c328fa`) keeps only owner-authored content (`Create` of a Note/Article/Question, or own `Announce`), so any residual foreign boost fan-out / actor-doc noise does not surface in the home timeline (S36); (c) the follow-request UI surface is the dedicated `/local/v1/u/{handle}/requests` endpoint (`GetFollowRequestsAsync`) + notifications, not the outbox. **D3 (remote-actor direct GET) FIXED** (the S24-D3 actor-doc fallback). **D4 (remote-actor direct COLLECTION routes 404) FIXED `2804fb55`** (2026-09-22): `CollectionEndpointHandler` now proxies a cached remote actor's `outbox`/`followers`/`following` to the remote instance (new `ProxyRemoteActorCollectionAsync` helper); live-verified both directions on the dev1 stack (A↔B) — all three collection routes 200 (were 404), 0 console errors. All four defects resolved.
- **Found:** Interop suite A2 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S4](s04-communities-following-remote.md) (Communities "Following" tab drops REMOTE communities — same "Following-tab under-reports remote items" family), [S14](s14-signed-out-actor-detail-csp.md) (remote actor detail), [S18](s18-local-follow-timeline-empty.md) (local follow state), [S22](s22-follow-notification-not-created.md) (follow notifications)

## Symptom

Two **fresh** Iris instances (A, B). Accounts: `ii-a1`/`ii-a2` on A, `ii-b1` on B. Cross-instance person-follow (A2), verified on a **clean re-run** (stack `down -v` + `up -d`).

**The cross-instance follow edge is correct and delivered** in both directions:
- B `ii-b1/following` = `[https://qa-iris-a.luit.ink/ap/v1/u/ii-a1]`; A `ii-a1/followers` = `[…/ii-b1]` (B→A delivered).
- A `ii-a1/following` = `[https://qa-iris-b.luit.ink/ap/v1/u/ii-b1]`; B `ii-b1/followers` = `[…/ii-a1]` (A→B delivered).
- Actor-page buttons show **Unfollow** on both sides (correct).

**Three defects remain (all reproduce on a clean entry):**

### Defect 1 — Profile "Following" tab omits REMOTE actors (both instances)

- A `ii-a1/following` = `[ii-b1@B]` (wire correct) → A profile **Following tab: "Not following anyone yet."**
- B `ii-b1/following` = `[ii-a1@A]` (wire correct) → B profile **Following tab: "Not following anyone yet."**

The followed **remote** actor is absent from the profile Following tab on *both* instances, even though the collection and the actor-page button are correct. Person-follow analogue of [S4](s04-communities-following-remote.md) (Communities "Following" tab drops remote communities). The Following-tab read path drops **remote** (non-local-host) targets.

### Defect 2 — Spurious self-follow activity in the outbox

`ii-a1` (A) followed `ii-b1` (B). A `ii-a1/outbox` contains **two** `Follow` activities:
- `Follow → https://qa-iris-b.luit.ink/ap/v1/u/ii-b1` (the intended remote follow) ✓
- `Follow → https://qa-iris-a.luit.ink/ap/v1/u/ii-a1` (**self-follow — the actor following itself**) ✗

The self-follow does **not** create a local self-edge (A `ii-a1/followers` = `[ii-b1@B]` only, not `ii-a1`), and its target is a remote-host IRI, so it is inert/undelivered — but it is a garbage `Follow` activity published to the outbox. Suggests the follow handler resolves the **logged-in actor's own IRI** for a second (bogus) write alongside the correct remote-target write.

### Defect 3 — Remote-actor direct GET 404s (both directions)

| Request | Expected | Actual |
|---|---|---|
| `GET B /ap/v1/u/ii-a1` (remote actor on B) | 200 actor | **404** (empty body) |
| `GET A /ap/v1/u/ii-b1` (remote actor on A) | 200 actor | **404** (empty body) |
| `GET B /ap/v1/u/ii-b1` (local control) | 200 | 200 ✓ |

A followed remote actor's document is not retrievable by direct IRI on the follower's instance, even though the follow flow just resolved and fetched that exact IRI (directory lookup → actor page → follow).

## Root cause (suspected)

1. **Following tab:** the profile Following-tab data path resolves only **local** actors (or drops items whose target IRI host ≠ the instance host), so a followed remote actor never renders in the list — while the actor-page button (which checks the edge directly) is correct. Compare S4.
2. **Self-follow:** the follow action writes a second `Follow` activity whose object is the **logged-in actor's IRI** rather than the **target remote actor's IRI** (subject/actor IRI mix-up in a secondary write path). It is not turned into a local edge, so it is harmless-but-garbage in the outbox.
3. **Remote-actor 404:** remote actor documents fetched during the follow flow are not stored under their IRI, so a later `GET /ap/v1/u/<remote-handle>` on the follower's instance 404s.

No `file:line` yet — needs a code pass on the Following-tab query (local-only filter?), the follow handler (dual IRI write), and the remote-actor object store.

## Fix (agreed approach)

- Following tab (and any Following list) must include **remote** followed actors, not just local ones — same fix family as S4.
- The follow handler must emit exactly **one** `Follow` activity, targeting the **remote actor's IRI**; never the logged-in actor's own IRI (no self-follow).
- Persist a followed remote actor's document under its IRI so `GET /ap/v1/u/<remote>` returns the actor (200) instead of 404.

## Re-verify (clean entry)

Two fresh instances A, B; accounts `ii-a1` (A), `ii-b1` (B).
1. `ii-b1` (B) follows `ii-a1` (A) via directory remote lookup (single click).
2. `GET B /ap/v1/u/ii-b1/following` = `[ii-a1@A]`; `GET B outbox` = **one** `Follow → ii-a1@A`, **zero** `Follow → ii-b1@B`. (Defect 2 guard for B.)
3. B profile → Following tab **shows `ii-a1@qa-iris-a.luit.ink`** (not "Not following anyone yet"). ← Defect 1
4. `GET B /ap/v1/u/ii-a1` returns **200** actor document (not 404). ← Defect 3
5. Mirror on A: `ii-a1` (A) follows `ii-b1` (B); A `ii-a1/outbox` = **one** `Follow → ii-b1@B`, **no** `Follow → ii-a1@A`; A Following tab shows `ii-b1@qa-iris-b.luit.ink`; `GET A /ap/v1/u/ii-b1` = 200.
6. 0 console errors on B and A during the flow.

**Re-verification evidence (Interop A2, clean re-run, 2026-09-20, QA stack):**
- Edge + delivery correct both directions (no self-edges).
- **Defect 1 confirmed both instances:** A Following tab = "Not following anyone yet" despite `following` = `[ii-b1@B]`; B Following tab = "Not following anyone yet" despite `following` = `[ii-a1@A]`.
- **Defect 2 confirmed:** A `ii-a1/outbox` = `Follow → ii-b1@B` **+ `Follow → ii-a1@A` (self-follow)**; A `ii-a1/followers` = `[ii-b1@B]` (self did not become a local edge).
- **Defect 3 confirmed:** `GET B /ap/v1/u/ii-a1` = 404 and `GET A /ap/v1/u/ii-b1` = 404; local control = 200.
**S24 OPEN (3 facets).**

## Re-test (fresh rebuild, 2026-09-20)

Re-ran A2 on the from-scratch stack (accounts `ii-a1`/`ii-a2` on A, `ii-b1` on B; cross follow `ii-a1`↔`ii-b1`):

- **Edge + delivery correct both directions:** B `ii-b1/followers` = `[ii-a1@A]`; A `ii-a1/followers` = `[ii-b1@B]`. Direct actor GET 200 both ways.

| Facet | Original (19:xx run) | Re-test (fresh) | Verdict |
|---|---|---|---|
| 1 — Following tab omits remote actors (both) | "Not following anyone yet" both sides | "Not following anyone yet" both sides despite correct `following` collection | **CONFIRMED** |
| 2 — spurious self-follow in outbox | `Follow → ii-a1@A` present | `ii-a1`'s A→B Follow object = `ii-b1` (correct remote IRI `…/ii-a1/follows/06GC1APX76ZGTS4B9P9DRRA1J4`); **no self-follow** | **NOT reproduced** |
| 3 — remote-actor direct GET 404 | 404 both ways | `GET A /ap/v1/u/ii-b1` = 200, `GET B /ap/v1/u/ii-a1` = 200 | **NOT reproduced** |

Defect 1 (Following-tab omits remote actors) is the **stable, reproducible** facet of S24. Defects 2 and 3 did not reproduce on the fresh build — either fixed, or flaky/order-dependent in the original run. **S24 OPEN (1 facet confirmed; 2 not reproduced).**

## Re-test (interop A2, 2026-09-21, fresh QA cluster)

Re-ran A2 (cross follow `ii-a1`↔`ii-b1`) on the rebuilt QA cluster.

- **Facet 1 — Following tab omits remote actors: CONFIRMED (both instances).** A `ii-a1/following` = `[ii-b1@B]` (wire) but A profile **Following tab = "Not following anyone yet."** B `ii-b1/following` = `[ii-a1@A]` (wire) but B profile **Following tab = "Not following anyone yet."**
- **Facet 2 — foreign activity in outbox: CONFIRMED (variant).** A `ii-a1/outbox` contained a `Follow` activity whose **actor = ii-a1@A** (foreign, id on A's host) that had been **stored in ii-b1's local outbox on B** (B `ii-b1/outbox` listed a Create/Follow whose object was ii-a1's note on A's host). The relationship state is still being persisted to the wrong actor's outbox. (Same "outbox stores foreign activities" family as the original self-follow facet.)
- **Facet 3 — remote-actor direct GET 404: NOT reproduced.** `GET A /ap/v1/u/ii-b1` = 200; `GET B /ap/v1/u/ii-a1` = 200.

**S24: Facet 1 OPEN (stable); Facet 2 OPEN (variant — foreign activity stored in local outbox); Facet 3 fixed.**

## Re-test (interop A2 re-verify, 2026-09-21, build `27b1ba6`)

Re-checked the existing `ii-a1`↔`ii-b1` cross-follow state on the current build (no new follow needed — the state was already established).

- **Facet 3 — remote-actor direct GET: NOT reproduced (fixed).** `GET A /ap/v1/u/ii-b1` = **200** (serves the stored remote actor doc). Consistent with the D3 fix (change 14818).
- **Facet 1 — Following tab omits remote actors: CONFIRMED, with a clearer root cause.** A profile **Following tab shows only the community `ii-a8-community`** — the remote person `ii-b1` is absent. The **wire** `GET A /ap/v1/u/ii-a1/following` = `[ii-a8-community]` **only** — i.e. **ii-b1 is NOT in A's `following` collection at all.** So the tab is faithfully rendering a **missing edge**, not dropping a present remote item.
- **The A→B follow edge is INCONSISTENT on the wire (new, sharper facet of S24):**
  - A `ii-a1/outbox` **has** `Follow → ii-b1` (id `…/ii-a1/follows/06GC3A27RBQ9CWG54D35PR3MW0`, published 01:50:26Z).
  - A `ii-a1/followers` = `[ii-a2@A, ii-b1@B]` (ii-b1 **is** A's follower).
  - B `ii-b1/following` = `[ii-a1@A]` (B follows A — consistent).
  - **But A `ii-a1/following` = `[ii-a8-community]` — ii-b1 is missing**, so A's `following` does not reflect A's own outbox Follow. The follow edge was delivered to B (B lists A as followed) and recorded in A's `followers`, but **A's own `following` collection never materialized the ii-a1→ii-b1 edge.**
  - The A outbox also still carries the **foreign** `Follow` activities minted on B's host (`…/ii-b1/follows/…`) addressed to `ii-a1` (B→A), i.e. A's outbox stores B-authored follow activities — the **Facet 2 variant** (outbox stores foreign activities) persists.
  - The earlier `Undo` of A's A→B follow (`…/ii-a1/undos/06GC3G26EMF1D44ZH05RA8NVXR` → `follows/06GC3A27RBQ9CWG54D35PR3MW0`, 02:16:38Z) means the A→B edge was **torn down** on A (which is why `following` no longer lists ii-b1), yet `followers` still lists ii-b1 — i.e. **A's `followers` and `following` are out of sync** after an unfollow/re-follow cycle.

**Verdict (build `27b1ba6`): Facet 3 fixed. Facet 1 + Facet 2 OPEN — the cross-instance follow edge is inconsistent: A's `following` collection omits ii-b1 (despite the outbox Follow + `followers` listing it), so the Following tab is empty of remote persons; A's outbox also stores foreign (B-authored) follow activities. The home-feed empty state (S36) is a separate, concurrent regression.**

## Re-test (fresh Follow, 2026-09-21, build `27b1ba6`) — disambiguates the UI defect from the stale wire state

Performed a **fresh Follow** (not relying on the prior cycle's state): as `ii-a1` (A), opened B's actor page `?iri=…/ii-b1` (which showed a **Follow** button — confirming ii-a1 did NOT currently follow ii-b1, consistent with the missing `following` edge) and clicked **Follow**.

- **Wire — the fresh Follow MATERIALIZES the edge correctly:** after the follow, `GET A /ap/v1/u/ii-a1/following` = `[c/ii-a8-community, u/ii-b1@B]` — **ii-b1 is now present**, and A's `following` and `followers` (`[ii-a2, ii-b1]`) are **in sync**. So a **clean follow works** on the current build; the earlier "A `following` missing ii-b1" was a **stale-state artifact of the prior unfollow/re-follow (Undo) cycle**, not a failure of the follow itself.
- **UI — Facet 1 (Following tab omits remote actors) PERSISTS regardless of wire state:** A profile **Following tab shows only the local community `ii-a8-community`** — the remote person **ii-b1 is still NOT rendered**, even though the wire `following` now **includes** ii-b1.
- **Decisive contrast — Followers tab renders the remote actor:** A profile **Followers tab shows both `ii-a2` (local) AND `ii-b1` (remote)** with Follow buttons. So the **Followers** tab resolves + renders remote actors, but the **Following** tab does **not**.

**Refined root cause (Facet 1):** this is a **UI rendering defect specific to the Following tab** — it omits **remote** actors (resolves/renders only local actors + communities), while the Followers tab handles remote actors fine. It is **not** (or not only) a wire/edge-sync problem: the wire `following` is correct after a fresh follow, yet the Following tab still drops the remote person. (The prior re-tests' "A `following` missing ii-b1" was a transient stale-state artifact from the Undo cycle — a secondary, separate state-consistency gap — but the **stable, user-visible** defect is the Following tab's failure to render remote actors.)

**Verdict (build `27b1ba6`, fresh follow): Facet 3 fixed. Facet 1 OPEN — refined to a UI Following-tab rendering defect (remote actors omitted; Followers tab renders them fine) — reproduces even when the wire `following` is correct. Facet 2 (foreign activities in local outbox) persists. The `following`/`followers` out-of-sync-after-Undo is a secondary state-consistency gap (transient; a fresh follow re-syncs it).**

## Re-test (Pass 116, 2026-09-21, current build `11fbec6`/`aebe420`)

**D1 (Following-tab omits remote actor) is now FIXED.** ii-a1 (A) follows the remote `ii-b1` (B) + the local community `ii-a8-community`.

- **UI — Following tab:** `GET /profile` → Following tab now renders **BOTH** — `ii-a8-community` (II-A8 Test Community, "Community" badge, Unfollow) **and `ii-b1`** (remote, Unfollow). (In Pass 104 the remote `ii-b1` was omitted from this tab while the Followers tab rendered it — that UI Following-tab rendering defect is gone.)
- **Wire:** `GET A /ap/v1/u/ii-a1/following` → `totalItems` = **2**: `…/qa-iris-a…/c/ii-a8-community` + `…/qa-iris-b…/u/ii-b1` (the remote actor edge is present).

**D2 (foreign activities in the local actor's outbox) STILL OPEN** (re-confirmed Pass 114): `GET A /ap/v1/u/ii-a1/outbox` contains **3 foreign (B) activities** (Announce + Create + Follow, actor `ii-b1`) — a remote actor's activities leaking into the local actor's outbox (outbox-integrity defect).

**Verdict (current build): S24 PARTIALLY FIXED — D1 (Following-tab remote-actor rendering) + D3 (remote-actor GET) now work; D2 (foreign activities in local outbox) is the remaining open facet.** **Status: OPEN (narrowed) — D2 only.**

## Re-test (Pass 154, 2026-09-21, build `38ae87c`) — D2 still OPEN and WORSE

- **D2 (foreign activities in the local actor's outbox): STILL OPEN, and the count has GROWN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **49**; the first page (20 items) contains **14 foreign (ii-b1) items** (was 6 in Pass 144; 3 in Pass 116). Type mix on the page: 11 Create + 3 Update + 2 Follow + 1 Delete + 2 Announce + 1 Like, of which 14 reference ii-b1. The outbox-integrity defect (a remote actor's activities leaking into the local actor's outbox) is **persistent and accumulating** — foreign items keep being added on every cross-instance interaction, and they are not cleaned up.
- D1 (Following-tab remote-actor rendering) + D3 (remote-actor GET) remain fixed (no regression observed).

**Verdict (build `38ae87c`): S24 PARTIALLY FIXED — D1 + D3 fixed; D2 (foreign activities in local outbox) still OPEN and the foreign-item count has grown to 14 on the first page (was 6).**

## Re-test (Pass 158, 2026-09-21, build `38ae87c`) — NEW facet D4: a cached remote actor's COLLECTIONS 404 (actor doc 200, /proxy 200); D2 still OPEN (6 foreign on page 1, total 55)

- **D2 (foreign activities in the local actor's outbox): STILL OPEN.** `GET A /ap/v1/u/ii-a1/outbox` → `totalItems` = **55** (grew from 49 in Pass 154); first page (20 items) type mix `11 Create + 4 Update + 1 Remove + 1 Add + 1 Follow + 1 Delete + 1 Announce`, of which **6 are foreign (ii-b1 authored)** (`4 Create + 1 Follow + 1 Announce`). The outbox-integrity defect (a remote actor's activities leaking into the local actor's outbox) is **persistent and accumulating**. (The per-page foreign count fluctuates 14→6 as the page window slides over the growing outbox, but the total foreign population keeps growing: 3 → 6 → 14 → total 55.)
- **NEW facet D4 — a cached remote actor's direct COLLECTION routes 404 (both directions), while the actor doc 200s and the explicit `/proxy` 200s.**
  - `GET A /ap/v1/u/ii-b1` (remote actor **doc**) = **200** (cached — the D3 fix holds).
  - `GET A /ap/v1/u/ii-b1/outbox` = **404**; `GET A /ap/v1/u/ii-b1/followers` = **404**; `GET A /ap/v1/u/ii-b1/following` = **404**.
  - **Symmetric on B:** `GET B /ap/v1/u/ii-a1` (doc) = **200**; `GET B /ap/v1/u/ii-a1/outbox` = **404**; `GET B /ap/v1/u/ii-a1/followers` = **404**; `GET B /ap/v1/u/ii-a1/following` = **404**.
  - **Control (own instance works):** `GET B /ap/v1/u/ii-b1/outbox` = **200** (own-actor outbox); `GET A /ap/v1/u/ii-a1/outbox` = **200** (local-actor outbox). So it is **specifically the remote actor's collections** that 404.
  - **The `/proxy` path works:** `GET A /ap/v1/proxy/<B ii-b1 outbox IRI>` = **200** — i.e. the remote collection IS reachable, but **only via the explicit `/proxy` route**, not via the actor's natural collection path (`/ap/v1/u/<remote>/outbox`).
  - **Client-visible symptom:** on the A UI, rendering the remote actor `ii-b1` (e.g. its posts / a notification card that fetches its outbox) triggers `GET /ap/v1/u/ii-b1/outbox` → **404** (observed as a console error on ii-a1's `/notifications`, where an ii-b1 notification card fetches the remote actor's outbox). The client falls back to the actor doc (200) so the page still renders, but the 404 is a defect: **the server should proxy a remote actor's collection routes (`/outbox`, `/followers`, `/following`) to the remote instance, the same way it serves the cached actor doc + the `/proxy` route.**
  - **Root-cause (suspected):** the remote-actor cache stores the **actor document** under its IRI (so `GET /ap/v1/u/<remote>` 200s — the D3 fix) but does **not** register/serve the actor's **sub-collection routes** (`/outbox`, `/followers`, `/following`), and the request handler does not proxy those sub-paths to the remote host. Only the dedicated `/proxy/{iri}` route forwards arbitrary remote IRIs. Fix = when the resolved actor is remote, proxy the trailing collection path (`/outbox`, `/followers`, `/following`, …) to the remote instance instead of returning 404.

**Verdict (build `38ae87c`): S24 PARTIALLY FIXED — D1 + D3 (actor doc) fixed; D2 (foreign activities in local outbox) still OPEN and accumulating; **NEW D4 (remote-actor direct collection routes 404 while the actor doc 200s + /proxy 200s) OPEN (S3, both directions, client-visible 404 on a remote actor's outbox/followers/following).
