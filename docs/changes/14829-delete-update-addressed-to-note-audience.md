# 14829 — S32 sending-side: address outbound Delete/Update to the note's original audience

- **Date:** 2026-09-21
- **Class:** bug / federation (S32, narrowed: sending-side audience)
- **Related:** [S32](../qa/s32-delete-not-propagated-peer-stale-copy.md) (peer-stale-copy risk), 14825 (receiving-side routing, `6b11799`)

## Problem

A local actor's cross-instance `Delete` (or `Update`) for a Note was emitted with `to`/`cc` = None — the activity was not addressed to the note's audience. A conforming receiver that reads `to`/`cc` to determine the distribution list sees an empty audience, and the peer does not reliably receive the activity as addressed. The receiving-side routing (14825, `6b11799`) already resolves a note-IRI recipient to the note's author, but the **sending side** still left the activity's audience empty.

## Root cause

`RewriteOutboundAudienceAsync` (the outbound audience rewrite, called at the outbox-publish path before the activity is stored) only handled `Create` and `Announce` — for a `Delete` or `Update` it was a no-op, so the activity kept the client's empty `to`/`cc`. The note's own `to`/`cc` (already populated by the Create's rewrite, which appends the followers to `cc`) was never copied onto the Delete/Update activity.

## Fix

Extend `RewriteOutboundAudienceAsync` with a `Delete`/`Update` case: read the stored object (the note, still live at rewrite time — the tombstone is applied later by the handler) and copy its `to`/`cc` onto the activity. A bare link reference (the common Delete shape), a missing object, or a Tombstone is a no-op (the audience cannot be recovered). New helper `ApplyStoredObjectAudienceAsync` encapsulates the read-and-copy.

The delivery mechanism (`DeletePropagationService` / `UpdatePropagationService` → `DeliverToActorAsync`) is unchanged and already targets the author's remote followers + the reply's parent owner + relays; this change makes the **serialized form** (the activity's `to`/`cc`) address the same audience, so a conforming peer that reads `to`/`cc` sees the correct distribution list.

## Tests

- New integration test `CrossInstanceTombstoneRetentionIntegrationTests.DeleteActivity_IsAddressedTo_NotesOriginalAudience`: alice posts a direct note to bob (`to`=[bob]), deletes it, and the stored Delete activity's `to` includes bob (the note's original direct recipient). Passes.
- Full `Iris.Server.Tests` suite: 1427 pass / 0 fail (25 pre-existing skips).

## Files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — `RewriteOutboundAudienceAsync` (Delete/Update case), new `ApplyStoredObjectAudienceAsync` helper.
- `tests/Iris.Server.Tests/CrossInstanceTombstoneRetentionIntegrationTests.cs` — new test + `using System.Text.Json`.

## Verification

Unit/integration green. Live two-instance re-verify (A posts → B fetches → A deletes → B's `GET <note IRI>` = Tombstone **and** A's outbox `Delete` carries `to`/`cc` = the note's audience) is handed to QA.
