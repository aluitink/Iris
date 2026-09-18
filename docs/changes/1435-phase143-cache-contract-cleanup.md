# 143.5 — Cache-contract cleanup (F-142.6/8)

**Commit:** `430d12b`
**Findings:** [F-142.6](../plans/phase-142-consistency-review.md) (S3) + [F-142.8](../plans/phase-142-consistency-review.md) (S3)

## F-142.6 — `?q` cache-key pollution on non-feed collections

`CommunityCollectionEndpointAsync` included `?q` in the cache key for **all** community collections, but only the `feed` handler actually passes `?q` into `feedService.GetFeedAsync`. For `members`/`followers`/`following`/`blocks`/`flags`/`mutes`, a `?q=` value produced a distinct cache entry rendering identical content — unbounded cache pollution per query string.

**Fix:** The `?q` suffix is now only computed when `collectionPath == "feed"` (`supportsQuery` flag). Non-feed collections ignore `?q` entirely (both for the cache key and the response).

## F-142.8 — Capability extensions not advertised on collection page-1 docs

`BuildCollectionPageDocument` already accepted `supportsRefresh`/`supportsQuery`/`supportsType`/`supportsDepth`/`namespaceIri` parameters, and `SerializeCollectionPage` emits `iris:refresh`/`iris:query`/etc. on page 1 when `namespaceIri` is non-empty. But only `FollowFeedHandler` and the public feed handler passed these flags. All other cacheable collections (actor outbox, community outbox, community collections, object replies) supported `?refresh=true` functionally but did not declare it.

**Fix:**
- Actor outbox (`CollectionEndpointHandler`): now passes `supportsRefresh: true, namespaceIri: ns`.
- Community collections (`CommunityCollectionEndpointAsync`): now passes `supportsRefresh: true, supportsQuery: supportsQuery, namespaceIri: ns` (query only for feed).
- Community outbox: now passes `supportsRefresh: true, namespaceIri: ns`.
- Object replies (`ObjectRepliesAsync`): now passes `supportsRefresh: true, namespaceIri: namespaceIri`. Gains a `namespaceIri` parameter, plumbed from `ObjectDocumentHandler`'s `optionsAccessor`.

The inbox endpoint (`no-store`, no refresh support) is intentionally left unchanged.

## Verification

- `dotnet build` clean.
- `dotnet test` green (1283/1283 Iris.Server.Tests on re-run; 2 known-flaky background-delivery tests failed on first full-suite run, passed in isolation).
