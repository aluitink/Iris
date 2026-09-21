# S32 — Delete (tombstone) emitted locally but NOT propagated to the peer; peer keeps a stale live copy

- **Class:** bug / federation — **Severity:** S2
- **Status:** open
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
