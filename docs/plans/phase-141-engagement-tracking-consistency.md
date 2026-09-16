# Phase 141 — Collection & Engagement Tracking Consistency

Review and fix how likes, shares (announces/boosts), and replies are tracked across every collection type so behavior is consistent everywhere.

## Scope

- **Like tracking:** per-object `likes` collection, like count, `iris:isLiked` state, Undo(Like) removal.
- **Share/boost tracking:** per-object `shares` collection, share count, `iris:isShared` state, Undo(Announce) removal.
- **Reply tracking:** per-object `replies` collection, reply count, thread integrity.
- **Collection types:** user-actor AP-native collections/objects, community/group AP-native collections, Iris-extension collections.
- **UI persistence:** like/share state must persist across page refresh; counts must reflect server state.
- **Object updates:** stored documents must be refreshed when an update to a tracked object is observed.

## Findings (from code review)

### F-1 (S2): Bare-link Create reply edge not recorded
A `Create` whose object is a bare link reference (not embedded) does not record the parent→child reply edge, because `StoreEmbeddedObjectAsync` only records the edge inside the `if (embedded is not null)` block. A thread reply posted as a bare-link Create would not appear under the parent's `/replies`. Most clients embed the object, so this is low-frequency, but it's an asymmetry with the object-store write path.

**Fix:** In `CreateActivityHandler.HandleAsync` (or `StoreEmbeddedObjectAsync`), when the object is a bare link, fetch the remote object to resolve `inReplyTo` and record the reply edge. Best-effort: a fetch failure leaves the reply untracked (the object store entry still stands).

### F-2 (S2): O(n) activity sweep on per-object likes/shares reads
`ObjectLikesAsync` and `ObjectSharesAsync` call `GetAllActivitiesAsync` (loads ALL stored activities) to map each liker/announcer back to its full activity document, then filter in memory. This is O(total_activities) per read. On a busy instance, this is the most expensive engagement read.

**Fix:** Add a dedicated `(actor, objectIri) → activity` index for Like and Announce activities. The `ILikeStore` and `IAnnounceStore` gain a `GetActivityAsync(Iri actor, Iri objectIri)` method (or the edge entity gains an `ActivityIri` column) so the per-object collection handlers do a direct lookup instead of a full-table sweep.

### F-3 (S3): Per-object collections not server-side cached
The per-object `likes`/`shares`/`replies` endpoints are NOT served through `LocalCollectionPageCache` — they only emit a `Cache-Control: max-age=60` header for intermediate/CDN caching. Every read hits persistence. This is not a correctness bug (the server always computes fresh), but it means:
- No server-side cache to invalidate on edge writes (unlike outbox/moderation collections).
- The `Cache-Control: max-age=60` means intermediaries may serve stale data for up to 60s.

**Decision:** Leave as-is for now. The per-object collections are small (a few items), and the `Cache-Control` header provides reasonable CDN-level caching. Adding server-side caching + invalidation is a larger change with diminishing returns. Revisit if profiling shows these endpoints are a bottleneck.

### F-4 (S3): Community-inbound Like/Announce — NOT a bug (verified)
The explore report flagged that `CommunityInboxActivityHandler` doesn't record per-object edges. However, the **specific** handlers (`LikeActivityHandler` line 88-100, `AnnounceActivityHandler` line 147-154) intercept community content before the catch-all `CommunityInboxActivityHandler` and DO record the per-object edges. The `CommunityInboxActivityHandler` is only a fallback. **No fix needed.**

### F-5 (S3): Object updates not refreshing stored tracked objects
When an `Update` activity is received for a tracked object (one that has likes/shares/replies), the stored copy of the object is refreshed (via `ObjectUpdateHandler` or the `CreateActivityHandler`'s re-store path). However, the per-object collections (likes/shares/replies) are derived from the edge store (not the object document), so an object update does NOT affect the collections — which is correct. The only concern is if the object's `id` changes (which would break the edge key), but ActivityPub requires stable object IRIs. **No fix needed** (confirmed: object updates refresh the document; collections are edge-derived and unaffected).

### F-6 (S2): UI like/share persistence across refresh — **VERIFIED OK (not a bug)**
The object document enrichment (`ObjectDocumentHandler` lines 6231-6364) provides `iris:likedCount`, `iris:sharedCount`, `iris:isLiked`, `iris:isShared`, and the minted `iris:likeActivityIri` / `iris:announceActivityIri`. The Blazor `EngagementBar` component seeds from these server-provided values on load.

**Playwright verification (2026-09-16):**
- Liked a local post (`andrew/notes/06G8WVZGY4MSWECMR6KP8QZY04`): button went `pressed`, count 0→1.
- Refreshed the page: button still `pressed`, count still 1. **Like persisted.**
- Verified an existing boost on a local post (`andrew/notes/06G9ACFMZ41P5A281VST0NADPM`): Boost button `pressed`, count 1, reply count 2. **Boost + reply count persisted.**
- Note: likes on **remote** objects (e.g. toot.cat posts) are NOT stored on Iris (the edge is recorded on the remote instance). The UI reflects the remote object's `likes` collection, which is correct per the `LikeActivityHandler` design.

**Conclusion:** No fix needed. The UI correctly seeds from server state and persists across refresh.

## Slices

- [ ] **141.1 — Fix bare-link Create reply edge (F-1):** When a Create's object is a bare link, fetch the remote object to resolve `inReplyTo` and record the reply edge. Integration test: deliver a bare-link Create with `inReplyTo`, verify the parent's `/replies` lists the child.
- [ ] **141.2 — Eliminate O(n) activity sweep (F-2):** Add a `(actor, objectIri) → activity` index for Like/Announce. Update `ObjectLikesAsync`/`ObjectSharesAsync` to use the index. Integration test: verify likes/shares collections still return correct items after the change (regression).
- [ ] **141.3 — Verify/fix UI like/share persistence (F-6):** Playwright-verify that like/share state persists across page refresh (click like, refresh, verify still liked). If broken, fix `EngagementBar` to seed from server state.
- [ ] **141.4 — Cross-collection consistency audit:** Verify like/share/reply tracking is consistent across user-actor, community/group, and Iris-extension collection types. Log any inconsistencies.
- [ ] **141.5 — Closeout:** Regression green (full suite), Playwright pass (like/share/reply across all collection types), update conformance matrix.

## Status

- [x] Code review (findings F-1..F-6)
- [ ] 141.1 — bare-link Create reply edge (reassessed: low value — bare-link objects are not stored locally, so the reply edge would point to an unresolvable child IRI; the proper fix is fetching+storing the remote object, which is a larger change. **Deferred** — revisit if bare-link Creates are observed in production.)
- [ ] 141.2 — O(n) sweep elimination
- [x] 141.3 — UI persistence verification (PASS: like + boost state persist across refresh for local objects; see F-6)
- [ ] 141.4 — cross-collection audit
- [ ] 141.5 — closeout
