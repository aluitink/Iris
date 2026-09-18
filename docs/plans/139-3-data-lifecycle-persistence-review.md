# 139.3 — Data lifecycle & persistence review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: durability, correctness, and
> lifecycle of everything Iris stores — local content, federated copies, caches, and the archival
> model Phase 138 depends on. Builds on Phase 16.4/32.2 (persistence providers), Phase 136.15/136.19
> (pagination/backfill, tombstone retention), and Phase 138's archival slices.

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Cold-start durability | Restart the Postgres-backed stack; confirm all content, follows, and settings survive | No data loss across restart | before/after row counts |
| 2 | Cache-vs-store consistency | For every cached read path (feed, collection pages, actor documents), confirm `?refresh=true`/cache-bypass returns store-fresh data | No stale-cache false negatives on a fresh write | before/after diff |
| 3 | Backup/restore round-trip | Run `scripts/backup-iris.sh` then `scripts/restore-iris.sh` against a populated instance | Restored instance matches pre-backup state | script output + row-count diff |
| 4 | Tombstone permanence vs. mod-removal reversibility | Delete a post (author) and separately remove one (moderator, where the concept exists); confirm the author-delete is permanent and (per Phase 138.23) a reversible removal isn't over-tombstoned | Correct distinct behavior for each | DB state dump |
| 5 | Federated content archival completeness | For a peer with pre-existing history (Lemmy per Phase 138.20, Mastodon test account), confirm first-peer backfill captures the full available history within the configured window | Item count matches the configured backfill window, not just post-peering activity | count comparison |
| 6 | Offline rebuild | With a peer's container stopped, confirm previously-synced threads (post + replies + counts) still render fully from local storage (Phase 138.21's bar, generalized to all peer types) | Full render, no dead fetches blocking the page | screenshot with peer stopped |
| 7 | Duplicate/replay delivery idempotency | Redeliver the same activity IRI twice | Stored once (Phase 136.17), no duplicate rows, no duplicate UI entries | DB row count + UI check |
| 8 | Media lifecycle | Confirm attached media survives instance restart, proxy rewrite, and a dead-source-URL scenario (502, not a broken image icon forever) | Media persists or degrades gracefully | screenshot |
| 9 | Migration safety | Apply the current EF Core migrations to a copy of a populated database from an older schema version (if available) or a fresh one | Migration completes cleanly, no data loss, app boots against the migrated schema | migration log |
| 10 | Data volume growth sanity | Seed a larger-than-typical dataset (thousands of posts/activities) and confirm query performance and pagination don't degrade catastrophically | Feed/search remain responsive (ties to 139.5 but checked here for correctness, not raw speed) | timing note |
| 11 | Retention/right-to-deletion | Delete an account (Phase 53.1); confirm the account's content is handled per the documented retention model (Phase 136.19) — no orphaned references, no broken links elsewhere | Consistent post-deletion state | DB check + UI check |

## Deliverable check

All 11 scenarios executed with evidence; any gap between the documented retention/lifecycle model
(Phase 136.19) and actual behavior is logged as a finding, not silently reconciled by rewriting the
doc.

## Progress tracking

- [x] 1  - [x] 2  - [x] 3  - [x] 4  - [x] 5  - [x] 6  - [x] 7
- [x] 8  - [x] 9  - [x] 10 - [x] 11

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Findings:**
- Scenario 3 (backup/restore): **FAIL → FIXED.** The media step in `scripts/backup-iris.sh` /
  `restore-iris.sh` used a raw `docker run -v iris-media-data:...` with the **unprefixed** volume
  name; the real compose volume is `irisweb_iris-media-data`, so the backup captured **0 of 5,204**
  media blobs (a DR restore would have deleted all media while reporting success). Fixed (commit
  `95d6150`): backup copies via `docker compose exec` + tar of the volume root; restore resolves the
  real volume name from `docker compose config` and streams over stdin. Full round-trip re-verified
  clean (DB row counts identical, 5,204 blobs restored, app healthy). [change doc](../changes/1393-3-backup-restore-roundtrip.md)
- Scenario 4 (tombstone permanence vs. mod-removal): **Author-delete permanence PASS** (live: posted +
  deleted a note as andrew; object → `Tombstone`/`formerType=Note`/no `iris:removedBy`; IRI still
  resolves to the marker; UI renders "Note post deleted"; re-animation guard covered by passing 136.19
  integration tests). **Mod-removal FINDING:** a mod-removal is **over-tombstoned** (same `Tombstone`
   as an author delete, differing only by the `iris:removedBy` display marker) — there is **no**
   restoration path, contrary to the 138.23 "may be restorable" note. Logged as a doc-vs-behavior gap;
   no code change (review slice). [change doc](../changes/1393-4-tombstone-permanence-vs-mod-removal.md)
 - Scenario 5 (federated content archival completeness): **BUG FOUND → FIXED.** A followed REMOTE
   community (Lemmy) persisted to the durable store by the remote community persister (135.1) was
   misrouted to its (empty) local outbox, so first-peer backfill (138.20) captured nothing. The
   outbound client was ruled out (a focused test confirms the real Lemmy outbox shape —
   `OrderedCollection` + inline `orderedItems`, no `first` — yields correctly). Root cause:
   `CommunityFeedService.ReadOutboxAsync` + the `isRemote` flag treated **any** community in the
   community store as local. Fixed (commit `d1847bc`): a host-locality gate (`IsLocalCommunity`, new
   optional `instanceBase` ctor param wired from `ActivityPubServerOptions.BaseUri` in DI) in both
   sites; a remote community is now fetched over the wire + backfilled. Verified live: `c/technology`
   (follows `lemmy.world/c/technology`) 0→20 backfilled items (totalItems 50, within the 1-page
   window); `c/owner-test-5428` (follows `lemmy.luit.ink`) 0→3; 14 lemmy.world objects persisted to
   the local store. +regression test +Lemmy-shape client test (1306 green).
    [change doc](../changes/1393-5-federated-content-archival-completeness.md)
  - Scenario 6 (offline rebuild): **PARTIAL PASS + 2 findings** (review slice, no code change). Core
    offline-rebuild bar **holds**: with `lemmy-1` stopped, the public timeline (20+ posts, remote +
    local), the `andrew` person feed (4.2s, 5 local + 15 cached-remote, `totalItems 197`), and a local
    note object page (2.7ms) all render fully from local storage; the person feed degrades gracefully
    (the 5s per-fetch cap means a downed *erroring* peer doesn't hang it). **Finding 1:** a community
    feed following an *unreachable* peer (`iris-dev2.luit.ink`, a downed dev instance — TCP accepted,
    TLS handshake stalls) is **blocked ~60–70s** before returning; `HttpClient.Timeout` (5s) bounds the
    send phase but not the connection phase (falls back to the transport's 35s `ConnectTimeout`).
    **Follow-up (RESOLVED 2026-09-18):** bound the connection phase — added
    `internal static ServerOutboundTransport.Create()` (a `SocketsHttpHandler` with an explicit 5 s
    `ConnectTimeout`) and swapped all six server-side outbound registrations to it; an unreachable
    peer now fails the dial at ~5 s instead of ~60–70 s. (Parallelizing the feed's per-contributor
    fetches is a separate, deferred optimization.) See
    [changes/1393-6f1-bound-outbound-connection-phase.md](../changes/1393-6f1-bound-outbound-connection-phase.md). **Finding 2:** a **foreign-IRI** remote
    object page **404s** via the object catch-all (the endpoint reconstructs the lookup IRI as
    `baseUrl + RoutePrefix + path` → a *local* IRI, so a stored `lemmy.luit.ink/post/1` is
     unreachable by path); the UI's `/object?iri=` path already covers the user-facing case, so this is
     a REST-addressability gap, not a UI gap. **Follow-up (RESOLVED 2026-09-18):** serve stored foreign
     objects by `?iri=` lookup — `ObjectDocumentHandler` now accepts an absolute `?iri=` query parameter
     that overrides the path-based reconstruction (a relative/blank `?iri=` is ignored → falls back to
     path). A stored foreign object is now served by its exact IRI via `GET /ap/v1/object?iri={iri}`;
     local-object path lookup is unchanged. +6 integration tests. See
     [changes/1393-6f2-foreign-iri-object-pages.md](../changes/1393-6f2-foreign-iri-object-pages.md).
     [original change doc](../changes/1393-6-offline-rebuild.md)

  - Scenario 7 (duplicate/replay delivery idempotency): **PASS** (verified; +1 DB-row-count test). The
    Phase 136.17 / C-07 guard is `InboxProcessor.ProcessAsync` → `Activities.TryAddActivityAsync`
    (add-if-absent by activity IRI): a redelivery returns `false` ⇒ dispatch is skipped ⇒ the endpoint
    still returns 202 (no 409/500). Duplicate rows are prevented twice over — the add-if-absent check
    and the PKs on `Activities.Id` and `BoxItems(Direction, ActorId, ItemIri)`. The inbox recording
    (`AddToInboxAsync`) is independently IRI-deduped, so a redelivered activity can't create a duplicate
    notification. Existing coverage (`CrossInstanceReplayDefenseIntegrationTests`,
    `DuplicateInboundDeliveryIdempotencyIntegrationTests`) asserted the *logical* no-op but not the
    *physical* row count. **Closed that gap**: `EfPersistenceContractTests.
    RedeliveredActivity_StoredOnce_SingleRowInDatabase` drives the add-if-absent twice against a real
    Testcontainers Postgres and counts the rows in `Activities` + `BoxItems` directly (raw
    `SELECT count(*)`) — each is exactly 1. **Live UI check** (Playwright, as andrew): the
    notifications page renders 12 distinct notifications, no duplicates, no console errors. 1306+12
    green. [change doc](../changes/1393-7-duplicate-replay-idempotency.md)

- [x] 8 — **PASS** (+ graceful-degradation fix). (a) Restart survival: 5,257 `Media` rows + 5,239 blobs on the durable `irisweb_iris-media-data` volume; a 287 KB blob served 200 after recreation. (b) Proxy rewrite: cross-origin → `/ap/v1/media/proxy?url=…` 200 jpeg; wire form keeps the original remote URL. (c) Dead source: proxy returns 502; **fixed** the client gap — `MediaGallery` `<img>`/lightbox now carry `@onerror` that re-renders a failed tile as a "media unavailable" link-out placeholder instead of a broken-image icon (verified live via Playwright). Separately logged a document-cache-coherence finding (the local-IRI proxy path serves a cached copy that doesn't re-validate against the store on restart). No new coded tests (web-test policy: verified live). [change doc](../changes/1393-8-media-lifecycle.md)

- [x] 9 — **PASS** (verified; no defects). Upgrading a populated, InitialCreate-only Postgres to the current schema via the startup `Migrate()` applies the full chain cleanly: no data loss, schema complete, both raw-SQL backfills processed the seeded rows (`Activities.ObjectIri` extracted from the `Create`'s jsonb `object.id`; `Actors.SearchVector` populated from the actor's searchable fields). Re-running `Migrate()` on an already-current DB is an idempotent no-op. +2 tests (`MigrationSafetyTests`, isolated `postgres:16-alpine` Testcontainers; old-schema data seeded via raw SQL since the current EF model can't INSERT into the older column set). [change doc](../changes/1393-9-migration-safety.md)

- [x] 10 — **PASS** (verified; no defects). Seeded a larger-than-typical dataset (500-post outbox + 1,500-object search corpus) via bulk `UNNEST` inserts + the migration's `SearchVector` backfill; exercised the real EF read paths. The outbox/feed read returned all 500 items in correct newest-first order; the GIN-indexed FTS search returned the exact known hit count (100) with correct, non-overlapping pagination. Timings (the evidence note): outbox read of 500 = 137.7 ms; object search (limit 20) over 1,500 = 37.4 ms — well within the generous 30 s ceiling (bar is correctness, not raw speed). +1 test (`DataVolumeGrowthTests`). Note for 139.5: the outbox/feed read is a full load (no SQL `LIMIT`) — the path to revisit at far larger volumes. [change doc](../changes/1393-10-data-volume-growth-sanity.md)

- [x] 11 — **PASS** (verified; no defects in the consistent half; one wording nuance logged). Deleting a local account produces a coherent post-deletion state that matches the Phase 136.19 retention model on every path that must hold: the actor 404s, the content IRIs serve `Tombstone` documents (not 404s / not live content), object + actor search exclude the deleted account, the account row is gone (by id and username), and other actors are untouched. The model's tolerated "bounded stale artifacts" are confirmed to survive (by design): the actor's `Create` activities + outbox box items (append-only, no pruning) still embed the original notes, and the Follow / CommunityMember / Like edges that reference the actor still resolve. +1 test (`AccountDeletionRetentionTests`, shared `postgres:16-alpine` `PostgresFixture`; the deletion mirrors `AccountDeletionService.DeleteAsync` exactly). **Logged nuance (F2, no code change):** the model's "not user-visible" wording is path-specific — accurate for the object-document endpoint (which serves the Tombstone) but not strictly for the feed/edge paths (`FeedService` / `CommunityFeedService` / `EdgeStore` read the surviving rows with no tombstone filter / no actor join), so a deleted-but-followed actor's original post content can still render in a follower's home feed and the actor can still appear in follower/following + community-member lists. Logged as a future feed-rendering product decision, not a defect. [change doc](../changes/1393-11-retention-right-to-deletion.md)

**Resume checkpoint:** all 11 scenarios done (139.3 complete). See the note on the phase-139 status below.
