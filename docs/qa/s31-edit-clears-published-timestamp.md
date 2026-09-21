# S31 — Editing a Note clears its `published` timestamp (and the `Update` activity's object omits `updated`)

- **Class:** bug / data-integrity — **Severity:** S3
- **Status:** **fixed (2026-09-21, `45f3038`, change 14822)** — clean-entry re-verify on the rebuilt QA cluster (HEAD `27b1ba6`); `published` preserved + `updated` stamped. (The peer-copy-dropped side-effect is S32, tracked separately.)
- **Found:** Interop suite A9 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`)
- **Related:** A9 (Edit/Update). The Update itself federates correctly (B's copy reflects the edit) — this is a **timestamp** defect on the edited Note.

## Symptom

`ii-a1` (A) had posted `II-A4-1 hello cross-instance` (Note `…/ii-a1/notes/06GC0JFR051T63GG17JPSRW928`), with `published` = `2026-09-20T19:27:48.2258488Z` and no `updated`.

After editing the body to `II-A4-1 EDITED`:
- `GET A <note>` → `content` = `II-A4-1 EDITED` ✓, `updated` = `2026-09-20T19:54:56.572083Z` ✓, **but `published` = `None`** (was `19:27:48` before the edit).
- `GET A /ap/v1/u/ii-a1/outbox` → an `Update` activity (`id` `…/activities/06GC0RPGNAAJ7W1RBMFRA3S8ZM`), `object` = the Note with `content` = `II-A4-1 EDITED`, but the `object` **omits `updated`** (and `published`).
- **B** (proxy fetch of the same note) → `content` = `II-A4-1 EDITED`, `published` = `None`, `updated` = `19:54:56` — i.e. the cleared `published` **propagated** to B (the edit federated, but so did the cleared timestamp).

So editing a Note **clears its `published` timestamp** (the original creation time is lost) and the `Update` activity's object does not carry `updated`/`published`. The edit content federates correctly (no stale copy), but the Note loses its publication time.

## Root cause (suspected)

When a Note is updated, the handler replaces the stored Note with the incoming `object` (from the `Update`), and that `object` does not carry over the existing `published` (or the `Update` object is constructed without copying `published`/setting `updated`). The original `published` is dropped. No `file:line` yet — needs a code pass on the Update handler (does it preserve `published` and set `updated` on the merged Note?).

## Fix (agreed approach)

- On `Update`, the Note's existing `published` must be **preserved**, and `updated` set to the current time, on both the stored Note and the `Update` activity's `object`. (AP: `published` is immutable after creation; `updated` reflects the edit.)

## Re-verify (clean entry)

1. Post a Note; record its `published` (e.g. `19:27:48`).
2. Edit the body.
3. `GET <note>` → `content` updated, `published` **still `19:27:48`** (not cleared), `updated` set to the edit time. ← the fix
4. The `Update` activity's `object` includes `updated` (and `published`).

**Re-verification evidence (Interop A9, 2026-09-20, QA stack):** pre-edit note `published`=19:27:48, no `updated`; post-edit `GET A <note>` `content`=`II-A4-1 EDITED`, `updated`=19:54:56, **`published`=None**; A outbox has `Update` (object content edited, object omits `updated`); B proxy of the note shows `published`=None (cleared value propagated). **S31 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CONFIRMED — reproduces.** On the from-scratch stack, `ii-a1` posted `II-A9-1 edit-and-delete probe` (Note `…/ii-a1/notes/06GC1GR5XZG3ZRFDG8YH34J3TR`, `published`=`2026-09-20T21:40:01Z`). After editing the body to `II-A9-1 EDITED`:
- `GET A <note>` → `content`=`II-A9-1 EDITED`, **`published`=None** (cleared), `updated`=`2026-09-20T21:40:53Z`.
- A outbox (inline) has `Update` `…/activities/06GC1GYENFFA91M9W0N052X3RM`, object content edited.
- B's log: `Inbox rejected: unknown recipient …/notes/06GC1GR5XZG3ZRFDG8YH34J3TR` (the `Update` delivery, note-IRI object, is rejected at the peer — see S32); B's cached copy of the note nonetheless shows the new content + `published`=None via lazy refetch.

S31 OPEN (reproduces on a fresh build).

## Re-test (interop A9, 2026-09-21, fresh QA cluster)

**CONFIRMED — reproduces (timestamp defect), with an added federation side-effect.** `ii-a1` (A) posted `II-A4-1 hello cross-instance` (Note `…/ii-a1/notes/06GC3AWSHG64NJHJ24EM27HZSW`), then edited the body to `II-A9-1 edited cross-instance`.
- `GET A <note>` → `content` = `II-A9-1 edited cross-instance`, `updated` = `2026-09-21T02:14:20Z`, **`published` = None** (absent — the note has **only `updated`, no `published`**). The edited note's object keys: `attributedTo, content, dislikedCount, likedCount, repliedCount, score, sharedCount, id, to, type, updated, url` — **no `published`**.
- **UI no-refresh:** after Save, the object-detail page **still showed the old content** (`II-A4-1 hello cross-instance`) — the edit saved on the wire but the UI did not re-render.
- **Federation side-effect (new, see S32):** the `Update` was addressed to the note IRI and B's copy of the note was **removed** — `GET B <note>` → **404** after the edit (the note had previously federated and was fetchable on B before the edit). So the Update not only clears `published` on A, it also drops the peer's copy.

**S31 OPEN (reproduces on the 2026-09-21 fresh cluster; `published` cleared + UI no-refresh + peer copy dropped).**

## Re-verify after fix (2026-09-21, rebuilt QA cluster @ HEAD `27b1ba6`)

The QA cluster's `qa-iris-a`/`qa-iris-b` images were 3h old (pre-dating the S31/S33/S27 fixes), so I rebuilt the two Iris services from `interop-testing` HEAD (`27b1ba6`) before re-verifying — the first edit attempt on the stale build still showed `published=None` (confirming the build was behind), then the rebuild made the fix live.

Clean entry as `ii-a1` (A), fresh note `S31 re-verify v2 base` (Note `…/ii-a1/notes/06GC3VAXBN4MEP52TQF1N21CM0`), `published`=`2026-09-21T03:05:53.7578037Z`, no `updated`. Edited the body to `S31 re-verify v2 edited`:

- `GET A <note>` → `content`=`S31 re-verify v2 edited`, **`published`=`2026-09-21T03:05:53.7578037Z` (PRESERVED — unchanged)**, **`updated`=`2026-09-21T03:06:47.4042671Z` (STAMPED)**. ✅
- `published` preservation = `True`; `updated` set = `True`.

**S31 FIXED** — the timestamp defect (the S31 core) no longer reproduces on the current build. (Note: `GET B <note>` still 404s — that peer-copy-dropped behavior is **S32**, a separate finding; it does not affect the S31 verdict on A, the source of truth.)

## Re-verify (Pass 124, 2026-09-21, current build `aebe420`) — FIXED, holding

Fresh edit re-confirmed the fix holds on the current build. As `ii-a1` (A), edited note `…/ii-a1/notes/06GC48G96XE3WTTV3KK0D39QQ8` (body → `II-A7-3 reverify S28 remote boost shares [edited: S31 published-preservation reverify Pass 124]`):

- `GET A <note>` → `content` updated, **`published` = `2026-09-21T04:03:25.6232431Z` (PRESERVED — the original, unchanged)**, **`updated` = `2026-09-21T06:03:49.2965649Z` (STAMPED — the new edit time)**. ✅

**S31 FIXED (holding)** — a fresh edit on the current build preserves `published` and stamps `updated`. (The peer-side facet — B's cached copy going stale / the Update not being delivered to B — is **S32**, a separate finding.)
