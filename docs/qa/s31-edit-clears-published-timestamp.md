# S31 — Editing a Note clears its `published` timestamp (and the `Update` activity's object omits `updated`)

- **Class:** bug / data-integrity — **Severity:** S3
- **Status:** open
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
