# 14828 — A remote Like immediately materializes the Note's `likedCount` (S37)

- **Status:** done (unit-verified; live re-verify deferred to QA)
- **Slice:** S37 (remote Like stored but `likedCount` not materialized)
- **Related:** S28 (remote Announce `shares` count — the `sharedCount` analog, fixed separately), the Phase 151 `ObjectInteractionCountRefreshService` (background counter pre-computation)

## Problem

When a **remote Like** is applied to a Note, the like is correctly **delivered, processed, stored, and
rendered** (the Note's `/likes` collection `totalItems` increments and the object-detail UI shows
"N like"), **but the Note's denormalized `likedCount` property and the embedded `likes.totalItems`
both remain `0`/`None`**. A client reading the Note document (rather than fetching `/likes`) sees a
like count of 0 even though the like exists. Reproduced on two independent cross-instance Likes
([S37 finding](../qa/s37-remote-like-stored-but-likedcount-not-materialized.md)).

## Root cause

The per-object interaction counters (`iris:likedCount` / `iris:sharedCount` / `iris:repliedCount` /
`iris:dislikedCount` / `iris:score`) are **pre-computed** onto the stored object document by the
`ObjectInteractionCountRefreshService` on a fixed interval (default **30 s**; a startup pass runs
first). The object-document and collection-page read paths serve those pre-computed counters when
present (`TryReadStoredCounts`) and only fall back to a per-read reverse-index sweep when they are
**absent**.

The inbound `LikeActivityHandler` records the like edge in the `ILikeStore` (which makes the `/likes`
collection correct) but **never updates the object's denormalized counters**. So once a prior pass has
persisted a `likedCount: 0` for an object that had no likes, a subsequent remote like leaves that
stored value **stale** (still present, still `0`) until the next 30 s interval pass reconciles it. The
read path sees the present-but-stale counter and serves it, so `GET {note}` reports `likedCount: None`
/ embedded `likes.totalItems: 0` even though `/likes` correctly returns `totalItems: 1`.

## Fix

Materialize the counter **immediately** when a like edge is recorded (or removed), instead of waiting
for the periodic pass:

- **`ObjectInteractionCountRefreshService`** (`src/Iris.Server/Stores/ObjectInteractionCountRefreshService.cs`):
  new public `RefreshObjectCountsAsync(Iri objectIri, ct)` that re-computes the object's four counters
  (+ the derived `score`) from the reverse indexes and re-stores the object when a value changed
  (reusing the existing `WriteCountsIfChanged` / `SetInt` helpers). It is a no-op for a remote (not
  locally stored) object, a tombstone, or when no `iris:` namespace is configured.
- **`LikeActivityHandler`** (`src/Iris.Server/Inbox/LikeActivityHandler.cs`): after recording the like
  edge for a **local** liked object, it calls `RefreshObjectCountsAsync(objectIri)` so the object's
  `likedCount` is correct on the very next read.
- **`UndoActivityHandler`** (`src/Iris.Server/Inbox/UndoActivityHandler.cs`): after removing a like
  edge (an unlike), it calls `RefreshObjectCountsAsync(likedObject)` so the count decrements
  immediately (the inverse path).
- **DI** (`src/Iris.Server/ActivityPubServerExtensions.cs`): the refresh service is registered as a
  **singleton** (so the handlers can resolve it) in addition to the existing `AddHostedService`
  registration (which constructs a separate instance that runs the periodic + startup pass). Both
  handlers take the refresher as an **optional** constructor parameter, so a host that does not register
  it (and existing tests) are unaffected — in that case the count converges on the next interval pass.

The periodic refresh pass is unchanged and remains the safety net (it still reconciles any object whose
counters were not refreshed on the interaction path, e.g. replies).

## Tests

Two new tests in `tests/Iris.Server.Tests/Inbox/LikeActivityHandlerTests.cs`:

- `HandleAsync_RemoteLikerOfLocalObject_MaterializesLikedCountImmediately` — a remote actor likes a
  local Note; the stored object's denormalized `likedCount` is **1 immediately** (not a stale 0, not
  waiting for the 30 s pass).
- `HandleAsync_TwoRemoteLikersOfLocalObject_LikedCountIsTwo` — two distinct remote likers each
  materialize their like; the `likedCount` is **2** after the second (each Like refreshes the count, so
  it is never a stale undercount).

## Verification

- `dotnet build`: 0 warnings / 0 errors.
- `dotnet test` (fast, `--filter "Category!=Slow"`): **all suites green**; `Iris.Server.Tests`
  **1425 pass / 0 fail** (was 1421; +4 across this and related slices). The only failures are the
  pre-existing `Iris.LiveInterop.Tests` ones that require a live Lemmy on `localhost:8091`
  ("Lemmy container not reachable").
- Live re-verify deferred to QA (requires the two-instance federation stack: A likes a B Note; B's
  `GET {note}` reports `likedCount: 1` and embedded `likes.totalItems: 1` **immediately**, matching
  `/likes` `totalItems: 1`).

## Scope note

This fix addresses the **count-materialization** facet of S37 (the Note's `likedCount` / embedded
`likes.totalItems`). It does not change the like-storage or `/likes` collection behavior (already
correct), nor the inline Like-button count rendering (which reads the document and is fixed transitively
once the document carries the correct `likedCount`). The `sharedCount` analog for remote Announces is
S28's surface and is addressed separately.
