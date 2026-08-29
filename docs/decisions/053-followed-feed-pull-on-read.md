# 053 — Followed feed is pull-on-read, not a background cached timeline

> Resolved 2026-08-28. See [Changelog — Resolved Decisions](../CHANGELOG.md#resolved-decisions).

## Context

The project needed a home timeline for a user following other actors. The feed must reflect what those followed actors have posted and needs to work across both local and remote follows. The implementation needed to balance freshness against the simpler server model already in place.

The key design question was whether the feed should be precomputed and cached, or recomputed on each request from the current relationships and remote data.

## Decision

Iris computes the followed feed on read.

The server merges:

- the outboxes of local follows
- the fetched outboxes of remote follows

The merge is de-duped, deterministically ordered, and capped by feed-size limits. The feed is re-evaluated for every request rather than through a background polling service.

This choice avoids caching a stale timeline while the remote follow graph changes and keeps the server model aligned with the existing read-through cache patterns already used elsewhere.

## Alternatives considered

### 1. Background poller / precomputed feed cache

This would improve efficiency but would introduce a new async timeline-building system and would risk showing stale content until the next refresh window expires.

### 2. Ignore remote follows in the feed

This would make the home timeline incomplete and would diverge from a real social graph.

### 3. Merge the feed once and cache the rendered page

This is simpler but hides new remote content behind the cache TTL and makes the timeline less responsive.

## Consequences

- The followed feed reflects current remote and local state on each request.
- The implementation stays simple and avoids new background services in this phase.
- A remote failures or unresolved outboxes degrade gracefully: they contribute nothing instead of breaking the entire feed.
- Feed freshness is highest while computational cost is bounded by page and item caps.

## Code alignment

The implementation reflects the decision:

- `IFollowFeedService` computes a merged feed per request
- remote outboxes are read through the shared client collection path
- the feed is not served through the local page cache in the same way as local collections
- results remain capped and deterministic via `FeedOptions`

This is the correct minimal design for a read-through social feed in the current server architecture while leaving a background-feed optimization available for future work.
