# 096 — Phase 13 preparation: concrete sub-slice breakdown

> 2026-08-30 · Phase 13 (Live Federation Compatibility) · Phase preparation / sub-slice breakdown

## What was built

No new code. This change breaks Phase 13 into concrete sub-slices, distinguishing those that can be
completed in a CI-only environment from those that require live partner instances.

## Phase 13 sub-slices

### CI-testable (no live instances required)

| Sub-slice | Description | Status |
|-----------|-------------|--------|
| 13.1 | **Mastodon extension passthrough tests** — verify that Iris correctly forwards Mastodon-specific extension properties (`toot:emoji`, `sensitive`, `poll`, `options`, `votes`, `endsAt`, `closed`, `oneOfMany`) as opaque blobs when they appear in incoming activities/objects. This is the "don't drop unknown properties" guarantee (F-01, change 064) applied specifically to Mastodon extensions. | ✅ Done (change 097) |
| 13.2 | **`ld+json` accept behavior documentation** — confirm and document that Iris accepts `application/ld+json` on inbound (Decision #4) and produces `application/activity+json` on outbound. Add a regression test proving the accept behavior. | ✅ Done (change 098) |
| 13.3 | **Mastodon `Question`/poll inbound handling** — verify that a `Question` activity (Mastodon poll) is correctly deserialized, stored, and served by Iris (as an opaque object with the `Question` type). Add integration tests. | ✅ Done (change 099) |
| 13.4 | **Mastodon `sensitive` flag inbound handling** — verify that the `sensitive` boolean (Mastodon extension) is correctly forwarded as an opaque property when it appears in incoming objects. Add integration tests. | Pending |

### Live-interop (requires real partner instances)

| Sub-slice | Description | Blocked on |
|-----------|-------------|------------|
| 13.5 | Stand up the public instance and the dev partner instances (Mastodon, Lemmy, Threads). | Phase 9 FQDN + TLS |
| 13.6 | Verify interoperability with Mastodon, Lemmy, Threads, and the planned compatibility targets. | 13.5 |
| 13.7 | Record live findings, deviations, and required follow-up fixes. | 13.6 |
| 13.8 | Decide the CI gating/opt-in model for the live interop suite. | 13.7 |
| 13.9 | Run real-user enumeration against target instances via WebFinger, NodeInfo, and search-style discovery. | 13.5 |
| 13.10 | Assert server-to-external-server compatibility across signatures, content types, pagination, delivery, and error handling. | 13.5 |

## Decision

**Phase 13 is split into two tracks:** CI-testable sub-slices (13.1–13.4) that can be completed now,
and live-interop sub-slices (13.5–13.10) that are blocked on real infrastructure. The CI-testable
sub-slices are the next work items. The live-interop sub-slices remain blocked until Phase 9's FQDN +
TLS is in place and real partner instances are available.

## Test count

971 tests, 0 failures, 0 warnings (13.1 + 13.2 + 13.3 done, +10 tests).
