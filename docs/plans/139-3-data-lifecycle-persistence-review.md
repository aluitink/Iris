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

- [ ] 1  - [ ] 2  - [ ] 3  - [ ] 4  - [ ] 5  - [ ] 6
- [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** none started yet — begin at scenario 1.
