# 14825 — Shared-inbox Delete/Update of a Note routes to the note's author

**Slice:** Dev Queue — S32: Delete (tombstone) emitted locally but NOT propagated to the peer; peer keeps a stale live copy
**Finding:** [s32-delete-not-propagated-peer-stale-copy.md](../qa/s32-delete-not-propagated-peer-stale-copy.md)
**Commit:** `6b11799`

## Problem

A `Delete` or `Update` of a local note delivered to the author's shared inbox was dropped as
`unknown recipient <note-IRI>`. The shared inbox routed any non-content, non-Undo, non-Like activity
to its `object` — for a `Delete`/`Update` that's the note's IRI (a content object, not an actor) — so
`TryGetActorAsync(note-IRI)` returned false, the recipient check 404'd, and the peer's copy was never
tombstoned or refreshed (stale live Note).

This is the same class of defect as S27 (Like) and S33 (Undo of Follow): the shared inbox routes
non-content activities to a field that is not an actor IRI.

## Root cause

`SharedInboxHandler` had no `Delete`/`Update` branch. Both fell into the generic `else`, which
resolves the recipient from `typedActivity.Object`'s IRI. For a `Delete`, the object is a bare Link
to the deleted note (a content object); for an `Update`, it's the embedded updated note (also a
content object). Neither is an actor, so `TryGetActorAsync` fails and the delivery is rejected.

## Fix

Added an explicit `Delete`/`Update` branch in `SharedInboxHandler` (mirroring the S27 Like branch):

```csharp
else if (typedActivity is Delete or Update
    && typedActivity.Object is { } editObjects
    && FirstIriFromCollection(editObjects) is { } editedObjectIri)
{
    // The Delete/Update's object is the edited object (a content object, not an actor). Route to
    // the object's owner (its attributedTo) — the note's author — so the DeleteActivityHandler
    // tombstones / the UpdateActivityHandler refreshes the stored copy.
    if (await ResolveObjectOwnerForDeliveryAsync(editedObjectIri, ct) is { } noteOwner)
    {
        recipients.Add(noteOwner);
    }
    else
    {
        // Unresolvable owner: degrade to the object IRI (will 404 as unknown recipient, as before).
        recipients.Add(editedObjectIri);
    }
}
```

`ResolveObjectOwnerForDeliveryAsync` reads the object from the local object store and returns its
`attributedTo` (the note's author) if that actor is local; otherwise it returns null (the delivery
degrades to the object IRI and is dropped, as before — a remote note's Delete/Update is applied on
the note's home instance, not here).

## Tests

Two new integration tests in `SharedInboxIntegrationTests`:

1. `DeleteOfLocalNote_DeliveredToSharedInbox_RoutesToAuthorAndTombstonesNote` — a local note is
   stored; a `Delete` (bare Link to the note IRI) is delivered to the shared inbox; asserts the
   Delete is stored (not dropped) and the note is tombstoned.
2. `UpdateOfLocalNote_DeliveredToSharedInbox_RoutesToAuthorAndRefreshesNote` — a local note is
   stored; an `Update` (embedded updated Note with new content) is delivered to the shared inbox;
   asserts the Update is stored and the note's content is refreshed.

Both tests use `alice` (remote, hosted by A) to sign the delivery (B resolves alice's key from A's
actor doc over the wire). The activity's `Actor` field is `bob` (the note's owner) — the shared inbox
routes based on the note's `attributedTo`, not the activity's `Actor`, so the signer identity does
not affect routing.

## Verification

- Fast suite: 1416 pass / 0 fail (was 1413).
- Web suite: 108 pass / 0 fail.
- Build: 0 warnings / 0 errors.

## Live re-verify

Deferred to QA on the two-instance federation stack (dev does not run it). The QA re-test: create a
note on A, confirm it federates to B, delete the note on A, confirm B's copy is tombstoned (was: B
keeps a stale live Note).
