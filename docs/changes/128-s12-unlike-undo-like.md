# 128 — Sample Explorer: unlike (`Undo(Like)`) closes the §3.3 "unlike" surface decision

**Status:** DONE — the client gained `UnlikeAsync`, the server removed the like edge on `Undo(Like)`
(both the local outbox path and the remote inbound path), and the Object page's Like button now toggles
to Unlike (showing the logged-on actor's current like state). This closes the §3.3 "no unlike / undo-like
exists in the client API" item that the plan deferred as a *library surface decision* for a later round.

## Background — §3.3 "unlike"

The sample plan's §3.3 explicitly noted: *"No unlike / undo-like exists in the client API (only
`LikeAsync`). The `Like` type is from KristofferStrube.ActivityStreams; Iris does not model an
`Undo(Like)`. So the Object page's Like has no inverse — this is a library surface decision, not a sample
bug."* It was the one sanctioned next-round gap left after the §3.1/§3.2 audit.

Investigation showed the server **already had** the removal primitive (`ILikeStore.RemoveLikeAsync`,
implemented in both `FileBackedLikeStore` and `InMemoryLikeStore`) but **no handler called it** — so an
`Undo(Like)` was a silent no-op. The work was to wire the inverse end-to-end: client → server → UI.

## Change

- **`src/Iris.Client/IActivityPubClient.cs` + `ActivityPubClient.cs`** — `UnlikeAsync(actorId, objectId)`:
  builds an `Undo` whose `object` references the original `Like` by its deterministic IRI (the same
  `{actorId}/likes/{objectId}` IRI `LikeAsync` mints) and publishes it to the actor's own outbox (the
  party that made the like undoes it — a content object has no inbox of its own). The `Undo` gets its own
  deterministic `{actorId}/unlikes/{objectId}` IRI so a retried unlike dedupes.
- **`src/Iris.Server/ActivityPubServerExtensions.cs`** — the **local** path: `RecordUndoLocalAsync` gained
  a `Like` branch → new `RemoveLikeLocalAsync`, which calls `ILikeStore.RemoveLikeAsync` (the inverse of
  `RecordLikeLocalAsync`) and returns the object's owner (or the object IRI) as the remote-recipient hop.
  This is the path the in-process + same-instance unlike takes (a local actor's outbox publish).
- **`src/Iris.Server/Inbox/UndoActivityHandler.cs`** — the **remote inbound** path: a `Like` branch (via
  new `ResolveLikeEdgeAsync`) that removes the like edge when a *remote* actor undoes a like delivered to
  a local actor's inbox — mirroring the existing block/flag/follow branches.
- **`samples/SampleBlazorClient/Pages/ObjectPage.razor`** — the Like button now **toggles**: `HasLiked`
  (computed from the actor's `liked` collection, cache-bypassed so a re-load reflects the current state)
  drives the label (`Like` / `Unlike`) and the write (`LikeAsync` / `UnlikeAsync`). A failed like-state
  read is treated as "not liked" so the button stays usable.

## Tests

- **`tests/Iris.Client.Tests/UnlikeDeliveryTests.cs`** (new, 2 tests): `UnlikeAsync` POSTs an `Undo`
  (type `Undo`, id `{actor}/unlikes/{object}`, `object` = the original like IRI) to the actor's own
  outbox; and its `object` reference matches the exact like IRI `LikeAsync` mints.
- **`tests/SampleBlazorClient.Tests/S7ScreenTests.cs`** (new, 1 in-process e2e test): like → the object is
  in the actor's `liked` collection; unlike (202) → it is gone (cache-bypassed re-read).
- **3 `Iris.Server.Tests` stubs** (`IrisActorDocumentFetcherTests`, `FeedServiceTests`,
  `IrisRemoteCollectionFetcherTests`) updated to implement the new `IActivityPubClient.UnlikeAsync`.

## Verification

- `dotnet build` 0 warnings; **880/880** green (Iris.Client.Tests 112 → 114; SampleBlazorClient.Tests
  81 → 82).
- The in-process e2e test confirms the full cycle: like records the edge, unlike removes it — the
  `RemoveLikeLocalAsync` path (local) is exercised; the `UndoActivityHandler` like-branch (remote inbound)
  is covered by the same `ResolveLikeEdgeAsync` pattern as the block/flag branches.

## Notes

- **Two removal paths, both wired.** A local actor's unlike goes through the outbox publish handler
  (`RecordUndoLocalAsync`); a *remote* actor's unlike (delivered to a local actor's inbox) goes through the
  inbox `UndoActivityHandler`. Both now remove the like edge.
- **Cache-bypass on the like-state read.** `ObjectPage.IsLikedByActorAsync` reads the `liked` collection
  with `BypassCache = true` (the S4 relay-screen pattern) — otherwise a cached `liked` page would show
  stale like state after a toggle.
- **Delete is the remaining §3.3 item.** `BuildTombstone` exists but there is no `DeleteAsync` /
  `Undo(Create)` client method or tombstone model in the server — a larger lift (object-store removal +
  outbox cleanup + tombstone rendering), tracked as the next §3.3 slice.
