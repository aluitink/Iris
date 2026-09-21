# S39 — A-side author notifications are missing while the B-side author receives the same interactions (asymmetric inbound delivery)

- **Class:** bug / federation-delivery / data-integrity — **Severity:** S2 (notifications are a core feature; the A-side author gets **no** Like/reply/follow notifications at all)
- **Status:** open — **NEW (Pass 157, 2026-09-21, build `38ae87c`)**
- **Found:** Pass 157 (2026-09-21), QA cluster (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`), build `38ae87c` (rebuilt 2026-09-21T07:16:12Z)
- **Related:** [S36](s36-home-feed-omits-posts-and-is-polluted-with-actor-document-activity.md) (the **A→B delivery-to-cache gap** is the same inbound-delivery root cause surfacing in the home feed; S36's "A→B post not delivered into B's cache (proxy 200, note view 404)" is the mirror of this finding's "B's activities not delivered into A's cache/inbox"), [S27](s27-like-dropped-at-shared-inbox-no-local-recipient.md) (remote Like dropped at shared inbox — now fixed for the author-routed case; this is a *different* leg: B→A), [S24](s24-cross-instance-follow-state-inconsistent.md) (D2 foreign activities in the local outbox — a related state-inconsistency family), [S22](s22-follow-notification-not-created.md) (local follow notification — fixed; this finding shows the **local** Like/reply leg is *also* missing on the A side)

## Symptom

On build `38ae87c`, the **A-side author (`ii-a1`) receives NO notifications** for interactions on their content, **while the B-side author (`ii-b1`) receives the equivalent interactions from A as notifications.** The delivery is **asymmetric**: A→B interactions notify B, but B→A interactions do **not** notify A.

Three concrete actions were taken this pass; each is persisted (wire-confirmed) but **none** produced a notification for the A-side author `ii-a1`:

1. **Local Like (A→A):** `ii-a2` (A) Liked `ii-a1`'s note `06GC63QMB` (II-S31-5). The wire confirms the Like applied: `GET A …/notes/06GC63QMB` → `…/ns#likedCount: 1`, `…/ns#score: 1` (namespaced count keys; the S37 count-materialization fix is live). **But `ii-a1`'s notifications = 0** — no "liked your post".
2. **Local reply/mention (A→A):** `ii-a2` (A) replied to the same note with an `@ii-a1` mention (HTTP 202). **But `ii-a1`'s notifications = 0** — no "replied to your post". (The `@ii-a1` mention did **not** resolve to a mention tag: the reply's `tags = []`, `to = [Public]` — but the reply is threaded under the parent via `inReplyTo`, so the parent author should be notified regardless.)
3. **Cross-instance reply/mention (B→A):** `ii-b1` (B) replied to `ii-a1`'s note `06GC5MR7` (II-S37-5) with an `@ii-a1` mention (HTTP 202, create IRI `06GC6MZVEYFYK4941GXWY1CFVG`, note id `…06GC6MZVEYFYK4941GXWY1CFVM`). The reply **federated and threaded** on A: `GET A …/notes/06GC5MR7` → `…/ns#repliedCount: 2`, and the reply's `inReplyTo` = the A parent IRI (`06GC5MR7…`). **But `ii-a1`'s notifications = 0** — no "replied to your post" from ii-b1.

**The asymmetry (decisive):** at the same moment, `ii-b1`'s (B) notifications page shows **22 unread** — including, from A's side, `ii-a1`/`ii-a2` **"sent you a follow request"** (09:43), `ii-a1` **"liked a post"** (04:35, 04:27), `ii-a1` **"replied … replying to ii-a1"**, and multiple `ii-a1` **"posted"** notifications. So **B receives A's interactions as notifications, but A does not receive B's.** The same interaction that notifies the B-side author is silent for the A-side author.

**Wire confirmation of the A-side silence:** `GET A /local/v1/notifications?limit=50&offset=0` (authenticated as `ii-a1`) → HTTP 200, `totalItems = 0`; `GET A /local/v1/notifications/unread-count` → `unread = 0`. This is after all three actions above (local Like, local reply, cross-instance reply) had persisted.

## Why this contradicts the earlier "notifications WORK" note

Pass 121 (build `aebe420`) recorded that **cross-instance notification delivery WORKS** — ii-a1's A notifications showed the fresh B reply + earlier boost/reply/like/follow. The difference: the QA cluster was **rebuilt at 07:16:12Z (data reset)** between Pass 121 and this pass, and the build moved `aebe420 → 38ae87c`. On the **current** build + fresh data state, the A-side notifications are **empty** while the B-side is populated — so this is either a **regression on `38ae87c`** or a **data/state-dependent** failure that the fresh in-process reproductions (like dev's S36 proof) don't capture. Either way it is a **live, user-visible defect** on the current build.

## Root cause (suspected — needs a dev code pass)

The pattern (B receives A's activity; A does **not** receive B's activity) plus S36's **A→B delivery-to-cache failure** (a fresh A post is proxy-200 but note-view-404 on B, i.e. **not stored in B's cache**) indicate an **inbound-delivery / shared-inbox gap that is directional**: activities are not being **delivered into the receiving instance's object store / inbox**, so the receiving instance's notification query (which reads from the local inbox) finds nothing.

Two candidate defects, either or both:

1. **B→A delivery does not persist into A's store/inbox.** When `ii-b1` (B) emits a Like/reply/Follow, the activity reaches A (the wire on A reflects `repliedCount`/`likedCount` updates — so *some* path applied it) but the **activity record itself** is not inserted into `ii-a1`'s **inbox** (`BoxItems` Direction=in) / `Activities` table on A, so the `/local/v1/notifications` query (which joins the author's inbox) returns 0. The count fields updating suggests a **lazy refetch/recompute** path (the S32 "lazy refetch" mechanism) is applying the *object* change without storing the *activity* for notification purposes.
2. **The local (A→A) Like/reply leg is also missing for the A-side author.** Even the fully-local `ii-a2 → ii-a1` Like (wire `likedCount=1`) and local reply did **not** notify `ii-a1`. That is a **local** notification gap (not a federation leg) — the local Like/reply handler is not creating a notification record for the local author, **or** the A-side notification query is filtering them out. (S22 fixed the local *follow* notification; the local *Like* and *reply* notification legs appear to be the missing analogues.)

The **asymmetry** is the strongest signal: if the A-side notification *query* were simply broken, B would likely also be affected (same code on both instances). B works, A doesn't → the failure is **state/data-specific to the A instance** (accumulated noise, the S36 edited-`Update` state, or a directional delivery gap) rather than a universal code bug. **A dev code pass comparing the A vs B store state (does the B→A Like/reply/Follow activity exist in A's `Activities`/`BoxItems`?) is needed to distinguish "activity not delivered/stored" from "activity stored but notification query filters it out."**

## Fix (proposed)

1. **Confirm the delivery leg:** verify whether a B→A Like/reply/Follow activity is **stored** in A's `Activities`/`BoxItems` (inbox) for `ii-a1`. If **not stored** → fix the B→A inbound delivery / shared-inbox so the activity is persisted (this is the same root cause as S36's A→B delivery-to-cache gap — fix the bidirectional delivery). If **stored but not surfaced** → fix the `/local/v1/notifications` query to include those inbox activities for the author.
2. **Fix the local Like/reply notification leg:** ensure a **local** Like and **local** reply (and mention) on a note creates a notification record for the note's author (the S22 fix covered local *follow*; extend the same path to local *Like* and *reply*/*mention*).
3. **Resolve the mention tag:** the `@ii-a1` mention did not resolve to a `Mention` tag (`tags=[]`, `to=[Public]`); it should resolve to the actor IRI so the mention is addressable (and so a mention-targeted notification is unambiguous).

## Re-verify (clean entry)

1. Fresh cluster; `ii-a1`↔`ii-b1` follow each other; `ii-a2` (A) follows `ii-a1` (A).
2. **Local Like:** `ii-a2` (A) Likes `ii-a1`'s note. → `ii-a1`'s `/notifications` shows a "liked your post" (Likes tab) + `GET A /local/v1/notifications?type=Like` returns it.
3. **Local reply/mention:** `ii-a2` (A) replies to `ii-a1`'s note with `@ii-a1`. → `ii-a1`'s `/notifications` shows a "replied to your post" (Replies tab).
4. **Cross-instance Like (B→A):** `ii-b1` (B) Likes `ii-a1`'s note (openable on B via `/object?iri=`). → `ii-a1`'s `/notifications` shows a "liked your post" from ii-b1.
5. **Cross-instance reply (B→A):** `ii-b1` (B) replies to `ii-a1`'s note with `@ii-a1`. → `ii-a1`'s `/notifications` shows a "replied to your post" from ii-b1 (Replies tab).
6. **Symmetry check:** for each of the above, the **A→B** mirror (ii-a1's note Liked/replied by ii-a2 → ii-b1's notifications) and the **B→A** leg both produce notifications on **both** sides. No more asymmetry.
7. 0 console errors on both notifications pages.

## Re-verification evidence (Pass 157, 2026-09-21, build `38ae87c`)

- **A-side silence:** `GET A /local/v1/notifications?limit=50&offset=0` (auth ii-a1) → `totalItems=0`; `unread-count` → `unread=0`. After: (a) ii-a2 local Like on `06GC63QMB` (wire `…/ns#likedCount=1`, `…/ns#score=1` — S37 count fix live); (b) ii-a2 local reply/mention (HTTP 202); (c) ii-b1 cross-instance reply/mention on `06GC5MR7` (HTTP 202, threaded — `…/ns#repliedCount=2`, `inReplyTo`=`06GC5MR7…`, `tags=[]`).
- **B-side populated (asymmetry):** `ii-b1` (B) `/notifications` → **22 unread**, incl. ii-a1/ii-a2 "sent you a follow request" (09:43), ii-a1 "liked a post" (04:35/04:27), ii-a1 "replied … replying to ii-a1", multiple ii-a1 "posted". **B receives A's interactions; A does not receive B's.**
- **Mention not resolved:** the `@ii-a1` mention in both replies did not resolve to a `Mention` tag (`tags=[]`, `to=[Public]`), though the replies are threaded under the parent via `inReplyTo`.

**S39 OPEN (S2) — A-side author notifications are missing (local Like, local reply, and cross-instance reply all silent for ii-a1) while the B-side author receives the equivalent A-side interactions (22 notifications). The asymmetry + S36's A→B delivery-to-cache failure indicate a directional inbound-delivery / shared-inbox gap. Awaiting a dev code pass to distinguish "B→A activity not stored in A's inbox" from "stored but filtered by the notification query", plus a fix for the local Like/reply notification leg.**

## Re-verification (Pass 159, 2026-09-21, build `38ae87c`) — fresh local-Like facet

- **Fresh local-Like repro (ii-a2 → ii-a1, same instance A):** as **ii-a2** (A) Liked ii-a1's note `06GC5MR7` (II-S37-5) via the object-detail Like button. The note's wire `…/ns#likedCount` went **1 → 2** + `…/ns#score` **1 → 2** (ii-a2's local Like **materialized immediately** — confirms the S37/S28 count fix holds for a 2nd, local, same-instance Like).
- **A-side still silent:** `GET A /local/v1/notifications?limit=10&offset=0` (auth ii-a1) → **`totalItems=0`, items 0** (unchanged from Pass 157/158). A same-instance Like produces a **count** but **no notification for the author**.
- **Verdict:** S39 **local-Like notification leg** confirmed OPEN with a clean fresh repro (independent of the cross-instance reply evidence in Pass 157). The author-notification inbound path is broken for **local Likes** too — not just cross-instance. S39 stays OPEN (S2).
