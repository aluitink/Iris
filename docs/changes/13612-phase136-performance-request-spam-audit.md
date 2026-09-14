# 136.12 — Performance and request-spam audit

**Date:** 2026-09-14
**Slice:** 136.12 (Lemmy interop — performance and request-spam audit)
**Status:** **COMPLETE (fix + triage).** Fixed the highest-impact finding (per-liker/announcer
full-table sweep) and triaged the remaining findings into blocker/bug/perf/observability classes.

## What this slice delivers

136.12's acceptance criteria:

1. For each critical scenario, count federated requests by method+path+trigger to detect duplicate fan-out.
2. Identify N+1 or redundant fetch patterns in community timeline and thread hydration.
3. Define acceptable request-count budgets for baseline scenarios.
4. Exit when high-noise patterns are triaged into blocker/bug/perf classes.

### Audit scope

The audit covered:

| Area | Key code | Request/query pattern |
|------|----------|----------------------|
| Outbox publish fan-out | `OutboxPublishHandler`, `RecordCreateLocalAsync`, `ResolveObjectOwnerForDeliveryAsync`, `RewriteOutboundAudienceAsync` | O(followers + relays) delivery jobs; duplicate remote object fetch (owner + audience) |
| Follow-feed hydration | `FeedService.GetFeedAsync` → `FetchRemoteOutboxAsync` | O(follows × PagesPerActor) remote outbox page GETs, sequential |
| Community-feed hydration | `CommunityFeedService.GetCommunityFeedAsync` | O((members + follows) × PagesPerActor) remote outbox page GETs |
| Public-feed hydration | `PublicFeedService.GetPublicFeedAsync` | O(actors) local-store reads, no wire fetches |
| Thread/replies hydration | `ObjectRepliesAsync` | Link-only items (no server-side N+1); client per-reply object GET (inherent to AP model) |
| Remote-object fetch caching | `ActivityPubClientFactory.Create` | Outbound client built without `Caches` → no object/page caching |
| Activity-store full sweeps | `ResolveLikeActivityAsync`, `ResolveAnnounceActivityAsync`, `GetRequesterActivityIrisAsync` | Per-liker/announcer `GetAllActivitiesAsync` sweeps (O(k × total_activities)) |
| Observability | `IrisDeliveryMetrics`, `IFederationTraceCollector` | Covers inbound inbox POSTs + outbound delivery; no read-side GET metrics |

### Findings (ranked by impact)

| ID | Area | Issue | Cost model | Class | Action |
|----|------|-------|-----------|-------|--------|
| F-136.12.8 | `/likes` `/shares` | Per-liker/announcer full activity-table sweep | O(k × total_activities) | **blocker** | **FIXED** (single sweep) |
| F-136.12.9 | Feed/outbox enrichment | Full activity-table sweep per requester-bearing page | O(total_activities) per page | perf (acceptable) | Documented (already a single sweep) |
| F-136.12.7 | Outbound fetch | Outbound client built without `Caches` | repeated wire GETs | perf | Triaged (follow-up) |
| F-136.12.1 | Outbox publish | Duplicate uncached remote object fetch | O(2) wire GETs per publish | perf | Triaged (follow-up) |
| F-136.12.3/5 | Follow/community feed | O(follows × pages) uncached outbox page GETs | linear in remote follows | perf | Triaged (follow-up) |
| F-136.12.2 | Outbox publish | O(followers + relays) delivery jobs, serialized | long delivery tail | perf (design) | Triaged (MaxConcurrentDeliveries) |
| F-136.12.6 | Thread/replies | Client-side per-reply object GET | inherent to AP model | inherent | Documented |
| F-136.12.10 | Observability | No read-side request metrics/trace | blind spot | observability | Triaged (follow-up) |

### The fix (F-136.12.8)

**Before:** `ObjectLikesAsync` and `ObjectSharesAsync` called `ResolveLikeActivityAsync` /
`ResolveAnnounceActivityAsync` per liker/announcer. Each call performed a full
`GetAllActivitiesAsync` sweep (O(total_activities)). For k likers, the total cost was
O(k × total_activities) with full row materialization + JSON deserialization per sweep.

**After:** A single `GetAllActivitiesAsync` sweep per endpoint (O(total_activities)), followed by
in-memory matching via a `Dictionary<Iri, IObjectOrLink>` keyed by the activity's actor IRI. The
per-liker/announcer resolve helpers (`ResolveLikeActivityAsync`, `ResolveAnnounceActivityAsync`)
are removed. The total cost is now O(total_activities) regardless of the number of likers/announcers.

**Behavior preserved:** The collection still serves full activity documents (id + actor + object)
when the activity is stored, and degrades to a `Link` to the liker/announcer when it is not (the
count stays exact). The `AudienceIriComparer` (case-insensitive ordinal) is used for the dictionary
key, matching the existing `GetLikersAsync` / `GetAnnouncersAsync` reverse-index semantics.

### New tests

Two integration tests added to `InteractionCollectionIntegrationTests`:

1. **`LikesEndpoint_MultipleLikers_WithStoredActivities_ReturnsFullLikeDocuments`** — seeds two
   `Like` activities in the activity store (bob + carol), verifies the `/likes` endpoint returns
   both as full Like documents (with the minted id and the correct actor), not bare actor links.

2. **`SharesEndpoint_MultipleAnnouncers_WithStoredActivities_ReturnsFullAnnounceDocuments`** —
   seeds two `Announce` activities in the activity store (bob + carol), verifies the `/shares`
   endpoint returns both as full Announce documents (with the minted id and the correct actor).

### Test count

Iris.Server.Tests: 1177 passed (1175 + 2 new), 16 skipped, 0 failed.
