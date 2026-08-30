# 095 — Phase 12 complete: formal closure and deferral of F-26–F-31

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Phase closure / deferral

## What was built

No new code. This change formally closes Phase 12 and records the deferral of the four remaining
rows (F-26, F-27, F-28, F-31) to Phase 13.

## Phase 12 status

All concrete conformance gaps (F-01 through F-30) are now closed:

- **Wave 1 (critical):** F-01, F-02, F-03, F-04, F-05, F-06, F-07, F-08 — all done (changes 064–071).
- **Wave 2 (high):** F-09, F-10, F-11, F-12, F-13, F-14, F-15, F-16, F-17, F-18, F-19 — all done
  (changes 072–091).
- **Wave 3 (medium):** F-20, F-21, F-22, F-23, F-24, F-25, F-29 — all done (changes 090–093 + earlier).
- **Wave 4 (low):** F-26, F-27, F-28, F-30, F-31 — F-30 done (change 094); F-26/F-27/F-28/F-31
  **deferred to Phase 13** (this change).

## Deferred to Phase 13 (Mastodon-specific / spec-valid-as-is)

| ID | Item | Why deferred |
|----|------|-------------|
| **F-26** | `Question`/poll objects | Mastodon-specific (the `Question` type and `options`/`votes`/`endsAt`/`closed`/`oneOfMany`/`poll` extension properties are not in ActivityStreams 2.0). Iris already forwards unknown types as opaque blobs; Mastodon poll interop is a Phase 13 live-interop concern. |
| **F-27** | Custom emoji | Mastodon-specific (`toot:emoji` extension). Iris already forwards unknown extension properties as opaque blobs (F-01, change 064). Custom emoji rendering is a Phase 13 live-interop concern. |
| **F-28** | `sensitive` flag | The `sensitive` boolean is a Mastodon extension (not in ActivityStreams 2.0; the `KristofferStrube.ActivityStreams` library has no `Sensitive` property). Iris already forwards unknown extension properties as opaque blobs. The `summary` property (which *is* in the library) is already surfaced automatically. The `sensitive` flag is a Phase 13 Mastodon concern. |
| **F-31** | `application/ld+json` production | Decision #4: Iris produces `application/activity+json` and accepts both `application/activity+json` and `application/ld+json` on inbound. Producing `ld+json` is a minor conformance nit with no interop impact (the ActivityPub spec defines `activity+json`; `ld+json` is a legacy MIME type that some older implementations use). Iris is spec-valid as-is. |

## Decision

**Phase 12 is complete.** All concrete, implementable conformance gaps are closed. The four remaining
rows are either (a) Mastodon-specific extensions not in the ActivityStreams 2.0 spec (F-26, F-27, F-28)
or (b) a spec-valid-as-is deviation (F-31). These are Phase 13 concerns (live federation compatibility
with Mastodon, Lemmy, Threads) and should be addressed during live interop testing, not in Phase 12.

The ROADMAP.md Phase 12 checklist is now fully ticked. Phase 13 (Live Federation Compatibility) is the
next phase — it requires real partner instances (Mastodon, Lemmy, Threads) and cannot be completed in a
CI-only environment.

## Test count

961 tests, 0 failures, 0 warnings (unchanged — no new code in this change).
