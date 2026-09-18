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

- [x] 1  - [x] 2  - [x] 3  - [x] 4  - [ ] 5  - [ ] 6
- [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11

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

**Resume checkpoint:** scenarios 1–5 done. Next: scenario 6 (offline rebuild).
