# S24 — Cross-instance follow: profile "Following" tab omits remote actors, spurious self-follow in outbox, remote-actor direct GET 404s

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open
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
