# 84.6 (part 3) — Cache-invalidation channel for multi-instance coherence

**Commit:** `f7b831f`
**Date:** 2026-09-11

## What was built

The **cache-invalidation channel** — the third (and last) convergence piece of Phase 84.6 (shared-state
scale-out). This closes the last gap: the in-memory actor/edge caches (`RemoteActorCache`,
`LocalActorDocumentCache`) are per-instance, so an actor-document change on instance A (a key rotation
re-stamping the document's `publicKey`, a profile change) left instance B's cached copy stale for up to the
cache's TTL (1 hour for the remote-actor cache). B served the old document (and resolved the old key from
it) — a silent divergence the single-instance guard (84.5) was designed to prevent.

### Key types

| Type | File | Role |
|------|------|------|
| `CacheInvalidationChannel` | `src/Iris.Server/Caching/CacheInvalidationChannel.cs` | A file-backed, cross-process invalidation journal (JSON lines + a cross-process file lock). `PublishActorInvalidationAsync` appends an event; `PollAsync(sinceSeq)` returns new events; `PurgeAsync` bounds the journal. Implements `ICacheInvalidationPublisher`. |
| `CacheInvalidationEvent` | (same file) | A journaled event: `Seq` (monotonic position), `ActorIri` (the invalidation key), `At` (UTC timestamp for the retention purge). |
| `ICacheInvalidationPublisher` | (same file) | The publisher seam: `PublishActorInvalidationAsync(Iri actorIri)`. The single-instance default is `NoopCacheInvalidationPublisher` (a no-op). |
| `CacheInvalidationService` | `src/Iris.Server/Caching/CacheInvalidationService.cs` | A `BackgroundService` (the `KeyProviderRefreshService` pattern): on startup + every `CacheInvalidationPollInterval` (default 5 s), polls the channel for new events and invalidates the local `RemoteActorCache` + `LocalActorDocumentCache` entries for the affected actor IRIs. Tracks an in-memory `lastSeq` cursor. |
| `ActivityPubServerOptions.CacheInvalidationPollInterval` | `src/Iris.Server/ActivityPubServerOptions.cs` | The poll interval (default 5 s; non-positive = startup-only). |
| `UseCacheInvalidationChannel` | `src/Iris.Server/ActivityPubServerExtensions.cs` | The scale-out extension: registers the `CacheInvalidationChannel` as `ICacheInvalidationPublisher` + the `CacheInvalidationService` hosted service. |

### Wiring

- **`AddActivityPubServer`** registers `ICacheInvalidationPublisher` as a no-op default (the single-instance
  default: with one instance there is no other instance's cache to invalidate).
- **`UseCacheInvalidationChannel(journalPath)`** replaces the no-op with the file-backed channel + the
  poller service.
- **`KeyRotationService`** injects the optional `ICacheInvalidationPublisher?` and calls
  `PublishActorInvalidationAsync(actorIri)` after `RotateAsync` re-stamps the actor document (best-effort: a
  publish failure is logged, never fails the rotation).

### Delivery guarantee

**At-least-once.** An event is journaled (and flushed) before `PublishActorInvalidationAsync` returns, so a
crash after the flush leaves it for replay. A reader that polls and then crashes re-reads the same events on
its next poll and re-applies the invalidation — a harmless no-op (invalidating an already-invalidated cache
entry is a no-op). The journal is bounded by the retention window (default 1 hour); a reader down longer than
the retention window misses events, but after an hour the cache entries are stale enough that a TTL expiry is
the expected recovery path.

### Test counts

**6 new integration tests** (`tests/Iris.Server.Tests/Caching/CacheInvalidationChannelScaleOutTests.cs`):

1. `RotateOnInstanceA_InstanceBCache_InvalidatedOnPoll` — the acceptance test: a rotation on A (re-stamping
   the shared actor document) → B's cache still serves the stale document (divergence) → B's
   `CacheInvalidationService` polls the channel → B's cache is invalidated → B re-fetches the fresh document
   (convergence, no restart).
2. `PublishAndPoll_CrossInstance_VisibleToOtherInstance` — a publish is visible to the other instance's poll
   (the shared journal); a reader's cursor (`sinceSeq`) filters to new events.
3. `Publish_MonotonicSequence_IncrementsPerEvent` — each publish appends an event with the next `Seq`.
4. `Purge_RemovesOldEvents_RetainsRecent` — events older than the retention window are purged; recent events
   are retained.
5. `Poll_TornJournalLine_Skipped` — a torn or corrupt journal line (a crash mid-write) is skipped; the next
   line is still valid.
6. `CacheInvalidationService_InvalidatesRemoteActorCache` — the service applies an invalidation event to the
   local `RemoteActorCache` (a subsequent read is a miss).

**Full suite:** 0 failed (Iris.Server.Tests 1053 → 1059).

## Design decisions

1. **File-backed journal (consistent with the 84.6 pattern).** The channel uses the same file-backed +
   cross-process-lock pattern as the `SharedDeliveryQueue` (84.6 part 2) and the `SingleInstanceLock` (84.5):
   a JSON-lines journal + a `.lock` file opened with `FileShare.None`. No new NuGet packages, no message
   broker — the simplest durable cross-process mechanism available.

2. **Poll-based (not push).** The `CacheInvalidationService` polls the channel on a fixed interval (default
   5 s) rather than being pushed events. A push (e.g. a named pipe, a socket) would be more responsive but
   more complex (connection management, reconnection, back-pressure). A 5 s poll is well within the cache
   TTL (1 hour), so the staleness window is bounded by the poll interval, not the TTL. A host that wants
   tighter bounds can set `CacheInvalidationPollInterval` to a smaller value (or call
   `CacheInvalidationService.PollOnceAsync` on-demand after a known actor-document change).

3. **The publish trigger is the key rotation (not every actor update).** The `KeyRotationService` is the
   clearest actor-document change (it re-stamps the `publicKey` extension, which changes the key IRI that B's
   inbound signature validation resolves from the cached document). Other actor-update paths (a profile
   change, a follow-accept) can publish via the `ICacheInvalidationPublisher` seam when they are implemented
   (the seam is in place). Wiring the publish into every actor-update path is out of scope for this slice
   (it would require touching the admin API + the follow-accept paths, which are separate slices).

4. **The `RemoteKeyCache` is not invalidated (keyed by key IRI, not actor IRI).** The invalidation event
   carries the actor IRI, not the key IRI. The `RemoteKeyCache` is keyed by key IRI (the remote key's IRI),
   so it cannot be directly invalidated from the actor IRI. But the actor-document invalidation (which
   triggers a re-resolve that fetches the fresh key) is sufficient: after B invalidates A's actor document,
   B re-fetches the fresh document (with the new `publicKey.id`) and resolves the new key (fetching it
   fresh, since the new key IRI is not in B's `RemoteKeyCache`). The old key IRI remains in B's
   `RemoteKeyCache` (harmless: B no longer uses it for the new signatures).

5. **The cursor is in-memory (not persisted).** A restart re-reads the journal from `Seq 0` (a harmless
   re-invalidating no-op for already-invalidated entries). Persisting the cursor (e.g. to a file) would
   avoid the re-read but add complexity (a per-instance cursor file, a cleanup path). The re-read is cheap
   (a bounded journal, a single file read) and the re-invalidation is a no-op, so the in-memory cursor is
   sufficient.

## Files changed

- `src/Iris.Server/Caching/CacheInvalidationChannel.cs` (new) — the channel + the event record + the
  publisher seam + the no-op default.
- `src/Iris.Server/Caching/CacheInvalidationService.cs` (new) — the background poller.
- `src/Iris.Server/ActivityPubServerOptions.cs` — `CacheInvalidationPollInterval`.
- `src/Iris.Server/ActivityPubServerExtensions.cs` — the no-op publisher registration + the
  `UseCacheInvalidationChannel` extension.
- `src/Iris.Server/Identity/KeyRotationService.cs` — the optional `ICacheInvalidationPublisher?` + the
  publish after `RotateAsync`.
- `tests/Iris.Server.Tests/Caching/CacheInvalidationChannelScaleOutTests.cs` (new) — 6 integration tests.
