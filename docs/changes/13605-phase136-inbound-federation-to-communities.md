# 136.5 — Inbound federation to Iris communities (Lemmy→Iris)

**Date:** 2026-09-14
**Slice:** 136.5 (Lemmy interop — inbound content federation to a local community)
**Status:** **COMPLETE (two gaps pinned).** The inbound-Create-to-community pipeline (handler →
community content recorder → member outboxes → feed projection) and the C-07 idempotency guard were
already fully in place and tested for the happy path. This slice closed the two remaining **test-coverage
gaps** for the Lemmy→Iris direction: (1) a federated note keeps the originator's `published` timestamp
in the community feed, and (2) a redelivered Create (same activity IRI) is recorded exactly once.

## What this slice delivers

136.5's acceptance criterion: *"a Lemmy community's member posts appear in an Iris community feed with
correct attribution, timestamps, and idempotency."* The pipeline itself was already built and happy-path
tested (`CommunityFollowingIntegrationTests` — a signed Create delivered to a community inbox reaches the
local member's outbox and the community feed). This turn pins the two attributes that test omitted.

### What already existed (verified, not rebuilt)

- **Inbound Create → community** — `CreateActivityHandler.HandleAsync`
  (`src/Iris.Server/Inbox/CreateActivityHandler.cs`): on a local-community recipient it calls
  `CommunityContentRecorder.RecordToMembersAsync`, which tags a copy of the Note (`attributedTo` carries
  the community IRI) and records it in each local member's outbox.
- **Timestamp source** — the Note's `Published` property, preserved on the tagged copy
  (`CommunityContentRecorder.TagNote` / the Create branch both carry `Published` through); backfilled
  from the Create (or `UtcNow`) only when absent.
- **Community feed projection** — `CommunityFeedService.GetFeedAsync` merges local member outboxes
  (newest-first, de-duplicated by activity IRI, filtered to community-tagged posts) and is served by
  `GET /ap/v1/c/{name}/feed` (`CommunityFeedHandler`); items serialize as full ActivityStreams objects
  (the Create with its `published`, the embedded Note with its `published`).
- **C-07 idempotency guard** — `InboxProcessor.ProcessAsync` (`TryAddActivityAsync` returns `false` when
  the activity IRI is already stored) runs **before** any handler dispatch, so it applies identically to
  the person and the community recipient paths (and every other activity type).
- **Happy-path coverage** — `CommunityFollowingIntegrationTests.RemoteContent_ToCommunityInbox_PropagatesToMemberAndAppearsInFeed`
  (a signed Create with **no** `published` reaches bob's outbox and the feed).

### New this turn: the two gaps

**`tests/Iris.Server.Tests/CommunityInboundContentIntegrationTests.cs`** — a two-instance test
(A: `alice`; B: `bob` + community `iris` with bob as its only local member), reusing the community-inbox
+ `DeliveryWorker` harness from `CommunityFollowingIntegrationTests`:

1. **Timestamps surface —
   `InboundCreate_WithPublished_SurfacesInCommunityFeed_WithOriginatorTimestamp`.** A Create whose
   activity **and** embedded Note carry a fixed `published` value (2026-01-15, well before "now") is
   delivered to the community inbox. The test asserts the note reaches bob's outbox and appears in
   `GET /ap/v1/c/iris/feed`, and that **both** the Create's and the Note's `published` round-trip to the
   originator's timestamp (within 1 s) — not the delivery time. A test that mistakenly used delivery
   time would fail, because the fixed stamp is years away from "now".
2. **Idempotency — `RedeliveredCreate_SameIri_RecordedExactlyOnce_InMemberOutboxAndFeed`.** The same
   Create (identical activity IRI) is delivered to the community inbox **twice**. The C-07 guard
   (shared, pre-dispatch) stores it once and the handler runs once, so the member's outbox holds the
   Create **exactly once** and the community feed surfaces it **exactly once**. This is the explicit
   community-inbound-Create lock for the guard that was previously only exercised on the edge-activity
   (Block/Follow) and person re-federation paths.

## What is NOT in this slice

- No production source change — the inbound-Create-to-community pipeline and the C-07 guard already
  work; this turn **pins** the two attribute gaps.
- No live Lemmy→Iris leg — that is blocked by the Lemmy-side signature-parse + egress gap (136.3) and the
  Lemmy→Iris WebFinger/egress gap (136.2), both documented as ops/Lemmy-side, not Iris code gaps. The
  Iris-side inbound contract (signature-validated Create → community feed, with timestamp + idempotency)
  is what 136.5 pins, against a synthetic remote peer (instance A) as the Lemmy stand-in.
- No dedicated community follow-request queue (that is 136.4's "not in scope" item, unchanged).

## Verification

- `CommunityInboundContentIntegrationTests`: **2 passed** (timestamp + idempotency).
- Full fast suite: **Iris.Server.Tests 1162 passed, 0 failed** (was 1159, +2 — no regression).
- Full solution build: **0 warnings, 0 errors** (`TreatWarningsAsErrors`).
- The pre-existing load-induced flake (`Follow_Unfollow_Refollow_Cycle`) did not trigger this run.
