# 141.4 — Cross-Collection Consistency Audit

## What was done

Audited engagement tracking (likes/shares/replies) across all three collection types — user-actor AP-native, community/group AP-native, and Iris-extension — to verify consistency. Fixed the primary inconsistency found.

## Findings

### F-141.4-1 (Fixed): Community feed/outbox not enriched with engagement counters

**Before:** `CommunityCollectionEndpointAsync` (community feed) and `CommunityOutboxHandler` called `BuildCollectionPageDocument` directly, bypassing `EnrichCollectionItemsAsync`. The actor outbox, actor feed, and public feed all enriched their items with `iris:likedCount`/`iris:sharedCount`/`iris:repliedCount`/`iris:dislikedCount`/`iris:score` + per-requester `iris:isLiked`/`iris:isShared`/`iris:likeActivityIri`/`iris:announceActivityIri`. Community collections did not.

**Impact:** The client `EngagementBar` uses a fast path (zero collection walks) when the server renders the counters on the item. Community-feed items lacked the counters, forcing the slower per-object `/likes`+`/shares` collection-walk fallback. Counts still rendered correctly (via fallback), but the performance and term-application were inconsistent.

**Fix:** Added `EnrichCollectionItemsAsync` calls to:
- `CommunityCollectionEndpointAsync` — enriched only when `collectionPath == "feed"` (the content collection; `members`/`following`/`followers`/moderation collections are actor lists, not content).
- `CommunityOutboxHandler` — always enriched (it's always content).

Both pass `requesterIri: null` (the community collection endpoints are not requester-specific; the per-requester terms are only rendered on the object-document path for an authenticated viewer).

**Files changed:** `src/Iris.Server/ActivityPubServerExtensions.cs` (2 call sites).

### F-141.4-2 (Documented, not fixed): Public feed excludes community-authored content

`PublicFeedService.GetPublicFeedAsync` filters to `Person` actors only. Community content is never in the instance-wide public feed. This is a deliberate design choice (communities have their own feed) and is not an engagement-tracking inconsistency. No action.

### F-141.4-3 (Documented, not fixed): Replies vs. likes/shares locality gate

`ObjectRepliesAsync` serves the collection when reply edges are known even if the parent object is not stored locally (Phase 136.7 cross-instance thread integrity). `ObjectLikesAsync`/`ObjectSharesAsync` 404 when the parent is not stored. This is a replies-vs-likes asymmetry (not user-vs-community) and is intentional: replies can be known from edges without the object document, while likes/shares require the activity documents which are only stored when the object was processed locally. No action.

### F-141.4-4 (Documented, latent): `isDisliked`/`dislikeActivityIri` not on collection items

The object-document path (`ServeObjectDocument`) renders `iris:isDisliked` + `iris:dislikeActivityIri` for the authenticated requester. The collection-item enrichment path (`EnrichCollectionItemsAsync`) computes `dislikedCount` but does not render the per-requester dislike state. Currently latent: no client component reads `isDisliked`/`dislikeActivityIri` from collection items (only the object document). Fixing requires new batch methods on the dislike store (`HasDislikedBatchAsync`) and a dislike-activity-IRI lookup — a larger change deferred to a future slice if the UI adds a dislike button.

## Verified consistent (no fix needed)

- **Edge store:** `EdgeStore` is author-type-agnostic (IRIs + `EdgeKind`). Likes/announces/replies recorded identically regardless of whether the object's author is a `Person` or `Group`.
- **Per-object collection endpoints** (`/likes`/`/shares`/`/replies`): identical code path for all author types; the only gate is "is the object stored locally?" (locality, not author type).
- **Object document enrichment:** `ServeObjectDocument` treats all stored objects identically; no author-type branch.
- **Inbox handlers:** `LikeActivityHandler`/`AnnounceActivityHandler`/`CreateActivityHandler` record edges without consulting the object's author type.
- **UI:** `EngagementBar`/`LemmyVoteBar` branch on Lemmy-vs-Iris provenance (IRI shape), not on author actor type. A user post and a community post with the same provenance render the identical component.

## Test counts

- Build: 0 warnings, 0 errors.
- Full suite: 1282 passed, 16 skipped, 1 failed (known flake `MutualPeeringHandshakeIntegrationTests`, passes in isolation).
- Community tests: 254/254 passed.
- Feed/outbox/enrichment tests: 205 passed, 11 skipped.
