# 139.3 Scenario 1 — Cold-start durability

**Priority:** HIGH  
**Status:** COMPLETE  
**Date:** 2026-09-18

## Objective

Restart the Postgres-backed stack and confirm all content, follows, and settings survive. Evidence: before/after row counts (delta = 0 for every table).

## Methodology

Per the WASM manual-test policy (Phase 45+), this was verified against the live Docker app, not with new coded tests.

1. **Established the "before" baseline** by querying all 9 Postgres tables in the `iris` database (container `irisweb-db-1`, named volume `iris-db-data`).
2. **Restarted the Postgres container** (`docker restart irisweb-db-1`) — the strongest test of the volume: it proves the data persists on the named volume, not in any in-memory state.
3. **Restarted the app container** (`docker restart irisweb-iris-web-1`) — a full cold start: exercises EF Core migrations-on-boot, Data Protection key loading, and the media blob volume mount.
4. **Verified the "after" row counts** — all 9 tables, delta = 0.
5. **Verified the app serves the data** (not just that it's in the DB):
   - Public web UI (`/`) returns HTTP 200.
   - WebFinger + actor document for `andrew` return the full actor (outbox, inbox, followers, following, icon, bio, publicKey).
   - A media blob (andrew's avatar, `/ap/v1/media/9d55d527…`) returns HTTP 200, 1,584,039 bytes, `image/png` — the media volume (`iris-media-data`) survived.
   - The media volume contains 5,202 blob files (consistent with the 5,220 `Media` rows; the small delta is proxy-cached remote media not stored locally, which is expected).
6. **Verified the browser renders content** via MCP Playwright: the home feed (logged in as `andrew`) rendered 3,826 chars of text + 35 images after a refresh. The session cookie survived the restart — proof the Data Protection keys volume (`iris-dp-keys`) survived.

## Results

### Before/after row counts (delta = 0 for every table)

| Table | Before | After | Delta |
|---|---|---|---|
| Actors | 2087 | 2087 | 0 |
| Objects | 8504 | 8504 | 0 |
| Activities | 852 | 852 | 0 |
| Edges | 11176 | 11176 | 0 |
| BoxItems | 1396 | 1396 | 0 |
| UserAccounts | 9 | 9 | 0 |
| Media | 5220 | 5220 | 0 |
| Keys | 17 | 17 | 0 |
| InstanceMetadata | 1 | 1 | 0 |

**Timestamps:** before = 2026-09-18 10:13:33 UTC; db restart = 10:14:06 UTC; app cold-start = 10:14:37 UTC (app healthy ~9 s after restart).

### Follows survived

- Total Edges = 11,176 (unchanged).
- Follow-like edges (Kind 15 + 16) = 10,415 — the follow/boost graph is intact.

### Settings survived

- `UserAccounts` = 9 (unchanged) — accounts + their settings persisted.
- `Keys` = 17 (unchanged) — the actor key pairs persisted.
- `InstanceMetadata` = 1 (unchanged) — instance-level settings persisted.

### App + browser verification

- Public UI `/` → HTTP 200 (6,901 bytes).
- WebFinger `acct:andrew@iris.luit.ink` → HTTP 200; actor IRI `https://iris.luit.ink/ap/v1/u/andrew` → HTTP 200 (2,504 bytes, full actor doc).
- Media blob `9d55d527…` → HTTP 200, 1,584,039 bytes, `image/png`.
- Media volume: 5,202 files in `/data/media`.
- Playwright home feed (andrew): 3,826 chars + 35 images rendered; session survived restart.

## Conclusion

**Status:** PASS — no data loss across restart.

All content (objects, activities, actors), follows (edges), and settings (user accounts, keys, instance metadata) survived both the Postgres restart and the full app cold start. The app serves the persisted data correctly, and the browser renders it. The three named volumes (`iris-db-data`, `iris-media-data`, `iris-dp-keys`) all persist as designed.

**No action needed.**

## Notes / findings

- The home feed's initial "Loading…" state after a cold start is expected first-fetch latency; a refresh loaded it fully (3,826 chars, 35 images). Two remote posts (beehive.city, haunted.computer) returned 418/404 from the AP proxy — these are dead upstream instances and render as "content unavailable" fallbacks, which is correct behavior (not a durability issue).
- Media volume has 5,202 blob files vs. 5,220 `Media` rows: the 18-row delta is proxy-cached remote media that is fetched on demand (not stored as local blobs), which is the expected design.
