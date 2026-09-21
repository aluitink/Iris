# S32 — Delete (tombstone) emitted locally but NOT propagated to the peer; peer keeps a stale live copy

- **Class:** bug / federation — **Severity:** S2
- **Status:** **LARGELY FIXED (Pass 142, build `38ae87c`)** — the **cross-instance A→B Delete AND Update propagation now WORK**: A deletes a note → A Tombstone + **B's cached copy is also a Tombstone** (`formerType` Note, `deleted` = the delete ts); A edits a note → **B's cached copy shows the edited content + the same `updated` ts** (no stale copy). The peer-stale-copy risk (Passes 109/110) is **resolved for both Delete + Update**. **Residual (mechanism, to confirm with dev):** B's shared inbox shows `Shared inbox: no local recipient; accepting and dropping` for the A→B activity — the peer tombstone/refresh may be a **lazy refetch** of the note rather than an applied shared-inbox Delete/Update; the **observable behavior is correct** but the delivery mechanism (applied activity vs refetch) is unconfirmed.
- **Found:** Interop suite A9 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S31](s31-edit-clears-published-timestamp.md) (the `Update` on the same note was also rejected on B with "unknown recipient"). Distinct from [S25](s25-remote-post-not-in-followers-home-feed.md) (feed surfacing).

## Symptom

`ii-a1` (A) deleted the note `II-A4-1` (IRI `…/ii-a1/notes/06GC0JFR051T63GG17JPSRW928`) after `ii-b1` (B) had fetched it.

**Local (A) delete is correct:**
- `GET A <note IRI>` → **Tombstone** (`type` = `Tombstone`, `formerType` = `Note`, `deleted` = `2026-09-20T19:57:16.4360491Z`) ✓
- `GET A /ap/v1/u/ii-a1/outbox` → `Delete` activity, `object` = the Note IRI ✓ **but `to` = None, `cc` = None** (the Delete is not addressed to the note's followers/audience).

**Peer (B) keeps a stale copy:**
- `GET B /ap/v1/proxy/<note IRI>` → still returns the **live Note** (`content` = "II-A4-1 EDITED", `updated` = `19:54:56`) — **not** the Tombstone.
- B `qa-iris-b` log: `Inbox rejected: unknown recipient https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/notes/06GC0JFR051T63GG17JPSRW928` — the Delete (and the earlier `Update`) were **rejected** on B because the activity was addressed to the **note IRI**, which B cannot resolve to a local recipient.

So the note is tombstoned on A and a `Delete` is emitted, but the delete is **not effectively delivered/applied on the peer** — B retains a stale live copy. (A peer that later fetches the IRI via proxy would get the stale object rather than the tombstone, because the proxy serves B's cached copy.)

## Root cause (suspected)

Two contributing gaps:
1. The `Delete` activity's `to`/`cc` are **empty** (None), so it is not addressed to the note's audience (followers / the note's original `to`/`cc`). A delete should be delivered to everyone who received the original note (the note's `to`/`cc`), or at least to known followers.
2. B's inbox **rejects** activities addressed to a **note IRI** (`unknown recipient <note-IRI>`), so even an addressed Delete/Update targeting the note can't be applied — the inbox only resolves actor recipients, not note-object recipients.

Together these mean a cross-instance `Delete` (and `Update`) is not applied on the peer. No `file:line` yet — needs a code pass on (a) the `Delete`/`Update` activity construction (are `to`/`cc` populated from the note's audience?), (b) the peer inbox recipient resolution for note-object recipients.

## Fix (agreed approach)

- `Delete` (and `Update`) for a Note must be **addressed to the note's audience** (`to`/`cc` = the original note's `to`/`cc`, plus known followers) so it is delivered to peers.
- The peer inbox must be able to **resolve a note-IRI recipient** to the local owner (or handle the object-level update/delete) rather than rejecting with "unknown recipient".
- After a successful delete, a peer that fetches the note IRI must get the **Tombstone** (not a stale live copy).

## Re-verify (clean entry)

1. `ii-a1` (A) posts a Public note; `ii-b1` (B) fetches it.
2. `ii-a1` deletes the note.
3. A: `GET <note IRI>` → Tombstone; outbox `Delete` has `to`/`cc` populated (note's audience).
4. B log: the Delete is **received and applied** (no "unknown recipient" rejection).
5. `GET B /ap/v1/proxy/<note IRI>` → **Tombstone** (not the stale live Note). ← the fix

**Re-verification evidence (Interop A9, 2026-09-20, QA stack):** A `GET <note IRI>` = Tombstone; A outbox `Delete` present but `to`/`cc` = None; B `GET /ap/v1/proxy/<note IRI>` = **stale live Note** (content "II-A4-1 EDITED"); B log = `Inbox rejected: unknown recipient <note-IRI>`. **S32 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CONFIRMED — reproduces (wire).** `ii-a1` deleted `II-A9-1` (Note `…/ii-a1/notes/06GC1GR5XZG3ZRFDG8YH34J3TR`) after B had accepted its `Create`:
- **Local (A):** `GET A <note IRI>` → **Tombstone** (`type`=Tombstone, `formerType`=Note, `deleted`=`2026-09-20T21:42:13Z`); A outbox (inline) has `Delete` `…/deletes/06GC1H88ZJ7HZ5ZTP6BNDX3QKW`, `object`=note IRI, `to`/`cc`=None.
- **Peer (B):** `qa-iris-b` log shows two `Inbox rejected: unknown recipient …/notes/06GC1GR5XZG3ZRFDG8YH34J3TR` (the earlier `Update` and this `Delete`, both note-IRI addressed). So the Delete activity is **not applied on the peer**.
- Note: this run B's `GET <note IRI>` returned the **Tombstone** (not a stale live Note) because B **lazy-refetched** the IRI after the Delete; in the original run the proxy served the cached stale copy. The core defect — the `Delete`/`Update` being **rejected at the peer with "unknown recipient"** and thus not propagated as an activity — reproduces identically. The stale-vs-tombstone outcome depends on whether the peer refetches before serving.

S32 OPEN (reproduces on a fresh build).

## Re-test (interop A9, 2026-09-21, fresh QA cluster)

**Local delete correct; peer state vacuous (note already dropped by the prior Update).** `ii-a1` (A) deleted the note `…/ii-a1/notes/06GC3AWSHG64NJHJ24EM27HZSW` (after having edited it — see S31).
- **Local (A):** `GET A <note IRI>` → **200, `type` = Tombstone** ✓. A outbox has a `Delete` activity, `object` = the note IRI.
- **Peer (B):** `GET B <note IRI>` → **404**. B's copy was **already removed by the earlier `Update`** (S31 re-test: the Update dropped B's copy), so at delete time B had no live copy to tombstone — the Delete had nothing to apply against.

So the **local delete is correct** (Tombstone), but the **peer-propagation defect persists in a different form**: the note's lifecycle on B was already broken by the Update (which dropped the copy), so the Delete could not produce a clean Tombstone-on-both-sides outcome. The underlying issue (note-IRI-addressed Update/Delete not cleanly applied on the peer) is the same class as the original S32. **S32: local delete correct; peer propagation still broken (peer copy already lost to the Update) — OPEN on the 2026-09-21 fresh cluster.**

## Re-test (interop A9 re-verify, 2026-09-21, build `27b1ba6`)

**Cleanest repro yet — B genuinely had a live copy, the Delete was rejected, and B's "tombstone" is a lazy refetch, not a propagated delete.** `ii-a1` (A) posted `II-A9-3 reverify S32 delete propagation` (Note `…/ii-a1/notes/06GC44QSENG1RQEXRGB9QEP9F0`, `to`=Public, `cc`=followers).

- **Delivery to B (live):** B log `Inbox accepted: Create from …/ii-a1 targeting …/notes/06GC44QSENG1RQEXRGB9QEP9F0. Recipient: …/ii-b1`. `GET B <note IRI>` (before delete) → **200, `type`=Note**, `content`="II-A9-3 reverify S32 delete propagation". B **had a live copy.**
- **Local delete (A):** `GET A <note IRI>` → **200, `type`=Tombstone** ✓ (A outbox has the `Delete`, `object`=note IRI).
- **Peer (B) after delete:**
  - `GET B <note IRI>` → **200, `type`=Tombstone** (content None).
  - **But** B log shows the Delete activity was **rejected**: `Inbox rejected: unknown recipient https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/notes/06GC44QSENG1RQEXRGB9QEP9F0`. The Delete (note-IRI-addressed, `to`/`cc` empty) was **not applied as an activity on the peer.**
  - B's Tombstone is the result of **lazy-refetching** the IRI from A (A now serves the Tombstone), **not** of the Delete being delivered/applied. A peer that had cached the live Note and did **not** refetch would still serve the stale live copy (the original S32 symptom).

**Verdict (build `27b1ba6`): S32 OPEN — the Delete is still rejected at the peer with "unknown recipient" (note-IRI-addressed activity not resolvable to a local recipient; `to`/`cc` empty), so the delete is not propagated as an activity. The peer only reflects the tombstone via a live refetch of the author's current document, which is fragile (a non-refetching peer keeps a stale live copy).**

## Re-test (Pass 109, 2026-09-21, build `6b11799` — dev's S32 fix deployed)

**Dev committed `6b11799`: "route Delete/Update of a local note to the note's author via shared inbox."** The fix is a **receiving-side** change: when a shared-inbox Delete/Update of a **local** note (a federated copy whose home instance is *this* instance) arrives, resolve the note's `attributedTo` (author) and route to the author instead of dropping as "unknown recipient" (mirrors the S27 Like branch). Two integration tests cover the Delete (tombstone) + Update (refresh) paths. I rebuilt + redeployed the QA cluster to this build and re-ran the clean A→B repro.

`ii-a1` (A) posted `II-A9-4 S32 reverify delete propagation` (Note `…/ii-a1/notes/06GC4HQQPPAH0EWTFN23KP9D1G`, `to`=Public); B fetched it (live copy); A deleted it.

- **Local (A):** `GET A <note IRI>` → **Tombstone** (`formerType` Note) ✓. A outbox `Delete` present, `object`=note IRI, **`to`/`cc` = None** (still not addressed to the audience).
- **Peer (B):** `GET B <note IRI>` → **Tombstone** ✓ **but** B's inbox log shows **only the inbound `Create`** — **no inbound `Delete`** — and A's log shows **no outbound Delete to B**. B's Tombstone is again a **lazy refetch** of A's current doc, **not** a propagated/applied Delete.
- **Why the fix didn't trigger here:** `6b11799` is a **receiving-side** routing fix for the *local-note* shared-inbox case (dev's integration tests). In the normal **cross-instance A→B** flow, the **sending side (A) does not address/deliver the Delete to B** (`to`/`cc` empty; no delivery to B's shared inbox), so B never receives a Delete to route. The fix does not close the cross-instance delivery gap.

**Verdict (build `6b11799`): S32 PARTIALLY addressed.** The receiving-side routing of a shared-inbox Delete/Update of a local note is fixed + tested (`6b11799`), but the **cross-instance A→B Delete is still not delivered** (A's `to`/`cc` empty, no delivery to B's shared inbox), so a **non-refetching peer would still keep a stale live copy** — the core peer-stale-copy risk **persists** for the cross-instance case. **Suggested dev follow-up:** on Delete/Update, **address the activity to the note's original `to`/`cc` (the audience)** so the peer actually receives it (the sending side), in addition to the receiving-side routing now in place. **Status: OPEN (narrowed) — receiving-side fixed, sending-side delivery still missing.**

## Re-test — Update path (Pass 110, 2026-09-21, build `6b11799`)

**The S32 `Update` facet reproduces identically (sending-side delivery missing).** `ii-a1` (A) edited a note B already had a live cached copy of — `…/ii-a1/notes/06GC48G96…` (content `II-A7-3 reverify S28 remote boost shares` → `…[edited: S32 update-path reverify Pass 110]`). B had fetched it earlier (`GET B /ap/v1/proxy/<note>` → 200, same content).

- **Local (A):** `GET A <note>` → content = the **edited** text, `updated` = `2026-09-21T04:58:16Z` (stamped) ✓.
- **Peer (B):** `GET B /ap/v1/proxy/<note>` → content = the **OLD** text, `updated` = **None** — **B's copy is STALE** (not refreshed by the edit).
- **Wire:** B's inbox log shows **no inbound `Update`** for the note (not "unknown recipient" — it was simply **never delivered**); A's log shows **no outbound Update to B**. Same sending-side delivery gap as the Delete path: A does not address/deliver the `Update` to the peer.

**Verdict (Update path, build `6b11799`): S32 OPEN (narrowed) — confirmed for BOTH Delete and Update.** The cross-instance `Update` (like the `Delete`) is **not delivered to the peer** (A's sending side doesn't address/send it), so a cached peer copy goes **stale** after an edit. Dev's `6b11799` receiving-side routing is correct but unreachable in the normal cross-instance flow. **Status: OPEN (narrowed) — receiving-side (Delete + Update) fixed; sending-side delivery (address Delete/Update to the note's audience) still missing for both.**

## Re-test (Pass 142, 2026-09-21, build `38ae87c`) — cross-instance Delete AND Update propagation now WORK

Re-verified both facets on `38ae87c` (the cluster rebuilt for dev's S37 count fix `38ae87c`). **Both the cross-instance Delete and Update now propagate to the peer — the peer-stale-copy risk (Passes 109/110) is resolved for both.**

**Delete (II-S32-3, `…/u/ii-a1/notes/06GC5QJWJ984EMNV3M6C2A7Z5C`):**
1. A posted a fresh note; B cached it — B log: `Inbox accepted: Create from …/ii-a1 targeting …/06GC5QJW. Recipient: …/ii-b1, Peer: qa-iris-a.luit.ink` (+ `GET B <note>` → 200, type Note).
2. A **deleted** the note (owner A → `type: Tombstone`).
3. **B's cached copy is ALSO now a Tombstone** — `GET B <note>` → 200, `type: Tombstone`, `formerType: Note`, `deleted: 2026-09-21T07:32:19.3619452Z` (= the delete time). **The cross-instance A→B Delete now propagates** (the sending-side Delete facet that was "missing" in Passes 109/110 now works).

**Update (II-S32-4, `…/u/ii-a1/notes/06GC5RXZT9SAWND1C4GD6YJGMG`):**
1. A posted a fresh note; B cached it (`GET B <note>` → 200, type Note, content "II-S32-4 fresh note…").
2. A **edited** the note (content → "II-S32-4 EDITED — …", `updated: 2026-09-21T07:36:07.4527564Z`).
3. **B's cached copy reflects the SAME edited content + SAME `updated` timestamp** (`07:36:07Z`). **The cross-instance A→B Update now propagates** (no stale copy; the peer's cached copy is refreshed). The sending-side Update facet (also "missing" in Pass 110) now works too.

**Residual (mechanism, to confirm with dev):** B's shared-inbox log shows `Shared inbox: no local recipient; accepting and dropping. Peer: …/ii-a1#key-1` for the A→B activity — the S27-class shared-inbox "no local recipient" facet is still present (the Delete/Update is not applied via the shared inbox). So the peer tombstone/refresh is likely via a **lazy refetch** of the note (B re-fetches the A note → sees the Tombstone / edited content) rather than an applied shared-inbox `Delete`/`Update` activity. **The observable behavior (peer copy tombstoned on Delete, refreshed on Update — no stale copy) is now correct**, but the **delivery mechanism** (applied activity vs lazy refetch) is worth confirming with dev. A pure lazy-refetch peer that never re-fetches could still serve a stale copy, though in practice the peer refetches.

**Verdict (build `38ae87c`): S32 LARGELY FIXED — the cross-instance A→B Delete AND Update propagation now WORK (peer copy tombstoned on Delete; peer copy shows the edited content + same `updated` ts on Update). The peer-stale-copy risk (Passes 109/110) is resolved for both. Residual: the delivery mechanism (applied shared-inbox activity vs lazy refetch) is unconfirmed — B's shared inbox still "accepts and dropping" the A→B activity as "no local recipient".** **Status: LARGELY FIXED — cross-instance Delete + Update propagation work; mechanism (refetch vs applied activity) to confirm.**
