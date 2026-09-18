# 13818 — Score Reconciliation Policy (138.18)

## Summary

Decided and implemented the score reconciliation policy for Lemmy interop. Iris keeps **separate** like
and dislike counters (mirroring Lemmy's `upvotes` / `downvotes`) and **derives** the Lemmy-equivalent net
score as `likedCount - dislikedCount` (the `iris:score` extension). This is rendered on the object document
alongside the existing `iris:likedCount` / `iris:sharedCount` / `iris:repliedCount` counters.

## Decision

**Keep separate counters + derived score.**

Rationale:
- Lemmy's net score is `upvotes - downvotes`, which maps directly to `likedCount - dislikedCount`.
- Keeping separate counters preserves the raw data (useful for UIs that want to show "5 up, 2 down"
  rather than just "3").
- The derived `iris:score` provides the Lemmy-equivalent net score for display, matching what Lemmy shows.
- This is consistent with the existing counter model (`likedCount`, `sharedCount`, `repliedCount`) — all
  computed at read time from reverse-index stores, not stored counters.

## Implementation

### New extension terms (`IrisExtensionTerms.cs`)

| Term | Type | Description |
|------|------|-------------|
| `iris:dislikedCount` | int | Number of distinct dislikers (cacheable, per-object) |
| `iris:score` | int | Net score = `likedCount - dislikedCount` (Lemmy-equivalent) |
| `iris:isDisliked` | bool | Per-requester: does the requester have a (net) dislike on this object? |
| `iris:dislikeActivityIri` | IRI | Per-requester: the IRI of the requester's minted Dislike activity (for Undo) |

### Object endpoint (`ActivityPubServerExtensions.cs`)

- Computes `dislikedCount` from `IDislikeStore.GetDislikersAsync(...).Count` (alongside the existing
  `likedCount` / `sharedCount` / `repliedCount`).
- Computes `isDisliked` from `IDislikeStore.HasDislikedAsync(...)` (alongside `isLiked` / `isShared`).
- Resolves `dislikeActivityIri` via a new `GetDislikeActivityIriAsync` helper (mirrors
  `GetRequesterActivityIrisAsync` for the Dislike case).
- Renders all four new extensions in `ServeObjectDocument` (the `score` is rendered whenever both
  `likedCount` and `dislikedCount` are available).

### Collection-page enrichment (`EnrichCollectionItemsAsync`)

- Adds `iris:dislikedCount` and `iris:score` to each embedded object (fetched per-object via
  `GetDislikersAsync`; a batch method can be added in a future optimization pass).
- Does **not** add per-requester `isDisliked` / `dislikeActivityIri` on collection items (the object
  endpoint already serves them; collection items are a lower-fidelity surface).

### Bug fix: `InMemoryPersistenceProvider.Reset()`

- The `Reset()` method was not clearing the dislike store (`_dislikes.Clear()` was missing). This caused
  stale dislike edges to persist across test resets. Fixed.

## Tests

3 integration tests (`ObjectDocumentScoreReconIntegrationTests`):

1. **`ObjectDocument_RendersDislikedCountAndScore`** — a note with 2 likes + 1 dislike renders
   `likedCount=2`, `dislikedCount=1`, `score=1`.
2. **`ObjectDocument_NoInteractions_RendersZeroDislikedAndZeroScore`** — a note with no interactions
   renders `likedCount=0`, `dislikedCount=0`, `score=0`.
3. **`ObjectDocument_MoreDislikesThanLikes_RendersNegativeScore`** — a note with 2 likes + 3 dislikes
   renders `score=-1` (the score can be negative, matching Lemmy's behavior).

All 3 pass. Full suite: 1243 passed, 0 failed.

## Files

- `src/Iris.Core/IrisExtensionTerms.cs` (modified) — 4 new terms
- `src/Iris.Server/ActivityPubServerExtensions.cs` (modified) — computation + rendering + helper
- `src/Iris.Server.InMemory/InMemoryPersistenceProvider.cs` (modified) — `Reset()` now clears dislikes
- `tests/Iris.Server.Tests/ObjectDocumentScoreReconIntegrationTests.cs` (new) — 3 integration tests
- `docs/plans/phase-138-lemmy-community-integration.md` (updated) — 138.18 marked `[x]`
