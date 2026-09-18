# 139.3 Scenario 4 — Tombstone permanence vs. mod-removal reversibility

**Phase:** 139.3 — Data lifecycle & persistence review
**Scenario:** 4 (of 11) — "Tombstone permanence vs. mod-removal reversibility"
**Date:** 2026-09-18
**Result:** **Author-delete permanence: PASS (verified live).** **Mod-removal reversibility: FINDING — a mod-removal IS over-tombstoned** (contrary to the 138.23 "may be restorable" note). Logged as a doc-vs-behavior gap; no code change (this is a review slice).

## What was tested

Two halves, per the scenario's pass criterion ("confirm the author-delete is permanent
and a reversible removal isn't over-tombstoned"):

1. **Author delete permanence** — post a note as `andrew`, delete it (author delete),
   confirm the tombstone is permanent and the IRI still resolves to the "deleted" marker.
2. **Mod-removal reversibility** — confirm whether a moderator removal produces a
   *restorable* (soft-hidden) state or is *over-tombstoned* (content gone, same as an
   author delete).

## Half 1 — Author delete permanence (PASS, verified live)

Live test on `iris.luit.ink`:
- Posted a note as `andrew` → IRI
  `https://iris.luit.ink/ap/v1/u/andrew/notes/06GB8JFGBDEXMNXRJX9V7RKWAC`
  (outbox 586 → 587; object stored as a live `Note`).
- Deleted it via the UI (object page → Delete → Confirm), which delivers a signed
  `Delete` to `andrew`'s outbox (the `DeleteActivityHandler` path).

**DB state after the delete** (the evidence dump):

| Field | Value |
|---|---|
| `ObjectType` | `Tombstone` |
| `IsTombstoned` | `t` |
| `has_removed_by` | `f` (no `iris:removedBy` — correct for an author delete) |
| `formerType` | `Note` |
| tombstone `deleted` | `2026-09-18T11:34:26.1853187Z` |

- **The IRI still resolves** (HTTP 200) serving the `Tombstone` document, not a `404`
  (the AS2.0 "deleted" marker, F-10).
- **The UI renders the deleted state** on a fresh load: "Note post *deleted* — This
  object was removed. Its content, replies, and interactions are no longer available."
  (Page title "Deleted post · Iris".) A same-SPA navigation right after the delete
  briefly showed the stale cached content; a full reload showed the correct tombstone —
  consistent with the existing SPA in-memory cache, not a defect.
- **Permanence (the re-animation guard, 136.19):** a late `Create`/`Update`/`Announce`
  for a tombstoned IRI must not resurrect it. Verified by the passing integration
  tests `CrossInstanceTombstoneRetentionIntegrationTests` (late Create *and* late
  Update both preserve the Tombstone) and
  `LemmyUpdatePropagationIntegrationTests.Update_TombstonedObject_IsNoOp`. The guard is
  in `CreateActivityHandler`, `UpdateActivityHandler`, and `AnnounceActivityHandler`
  (all "the Tombstone is the authoritative final state").

**Conclusion:** the author-delete is **permanent** — a Tombstone with no restoration
path, and the IRI keeps resolving to the marker. PASS.

## Half 2 — Mod-removal reversibility (FINDING: over-tombstoned)

The mod-removal code path (`DeleteActivityHandler`, lines 129 + 154-162) produces the
**same** `Tombstone` as an author delete. The *only* difference is the
`iris:removedBy` extension (set when the deleting actor is a community member who is
not the `attributedTo` owner):

- **Author delete** → `Tombstone` + `formerType`, **no** `iris:removedBy`.
- **Mod removal** → `Tombstone` + `formerType` **plus** `iris:removedBy` = the mod's IRI.

There is **no separate "soft-hidden-but-restorable" state**. Both paths call
`BuildTombstone` + `PutObjectAsync(tombstone)` — the content is gone in both cases. A
grep of `Iris.Server` for a restore/un-delete/re-animate-*enable* path returns none:
the only "restore" hits are unrelated (key rehydration, proxy retry, feed ordering,
dead-letter journal), and the 136.19 re-animation guards only *prevent* resurrection —
they never *enable* it.

**Live DB evidence:** all 8 tombstones on the instance have `has_removed_by = false`
(every one is an author delete — 5 local, 2 remote-author from mementomori.social and
mastodon.social, 1 the new 139.3-s4 test). There are **no** mod-removal tombstones
because mod-removal only occurs via a remote Lemmy moderator, and this instance has no
such peer. The mod-removal *code path* is verified by the passing integration test
`LemmyDeletionSemanticsIntegrationTests.RemoteMod_RemovesCommunityPost_TombstoneHasRemovedBy`
(which asserts the tombstone carries `iris:removedBy` = the mod's IRI).

### The gap (logged, not silently reconciled — per the scenario's deliverable)

The 138.23 doc (`IrisExtensionTerms.RemovedBy`, `IrisDocumentExtensions.GetRemovedBy`,
and the `DeleteActivityHandler` remarks) describes a mod-removal as content that is
"hidden by a moderation action and **may be restorable**." The implementation does **not**
implement any restoration: a mod-removal is over-tombstoned exactly like an author
delete, with `iris:removedBy` being a *display* marker only (so the UI can say "removed
by a moderator" rather than "deleted by author"). The "reversible" half of the scenario's
pass criterion therefore **fails as literally worded** — a reversible removal *is*
over-tombstoned.

This is a **doc-vs-behavior gap**, not a regression: the code and the docs agree that a
mod-removal tombstones the content; the docs overstate reversibility. Resolving it is
either (a) implement a real restoration path for mod-removals (a feature — out of scope
for this review slice) or (b) correct the 138.23 wording to "not restorable" (a doc fix).
Recorded here as a finding; no code or doc change made in this verification slice.

## Evidence summary

- **Live author-delete DB dump:** 8 tombstones, all `has_removed_by=false`; the new
  139.3-s4 note is a `Tombstone`/`formerType=Note`/no-`removedBy` (see table above).
- **Live API:** tombstoned IRI → HTTP 200, `{"type":"Tombstone","formerType":"Note",…}`.
- **Live UI:** "Note post deleted — This object was removed." (screenshot).
- **Re-animation guard:** passing integration tests (136.19).
- **Mod-removal code path:** passing `LemmyDeletionSemanticsIntegrationTests` (4 tests:
  author-delete, mod-removal, stranger-rejected, reply-edge cleanup).
- **No restoration path:** grep of `Iris.Server` confirms no un-delete/restore-enable code.

## Decision (autonomous, recorded per loop policy)

- Treated this as a **review** slice: verified the behavior, captured the DB state dump,
  and **logged** the mod-removal over-tombstoning gap rather than implementing
  mod-removal reversibility (a feature) or rewriting the 138.23 docs unilaterally.
- Did not add new coded tests — the existing integration suite
  (`LemmyDeletionSemanticsIntegrationTests`, `CrossInstanceTombstoneRetentionIntegrationTests`,
  `LemmyUpdatePropagationIntegrationTests`) already covers both halves at the code level;
  this slice adds the live-instance DB-state evidence the scenario asks for.
