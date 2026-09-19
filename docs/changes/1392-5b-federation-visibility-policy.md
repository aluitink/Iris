# 139.2-s5b — Federation visibility policy: store non-public content intact, hide on the read path

**Status:** Done (decision pinned by cross-instance integration tests).
**Closes:** Deferred surface #3 from 139.2-s5 (the federation visibility policy, Gap #2): "whether a
remote instance should *suppress* non-public content on receipt vs. *store it with a visibility
marker* so its own read path can filter it … Deferred as a federation product decision."

## The decision

When a non-public post (a direct message `to=[bob]`, or a followers-only post) is **federated** to a
remote instance, Iris:

1. **Stores it on receipt** — the `CreateActivityHandler` records the embedded object in the
   receiving instance's object store and in the named local recipient's outbox, unconditionally
   (no inbound audience check, no suppression). The inbound path does **not** run the outbound
   audience rewrite (`RewriteOutboundAudienceAsync` is outbound-only), so the stored note keeps its
   original `to`/`cc` **intact**.
2. **Hides it on the read path** — the S5 visibility filter (`VisibilityFilter`, wired into
   `PublicFeedService` and `GlobalSearchService`) drops the item for any requester who is not a
   named recipient (or the author). An anonymous / non-recipient request to the receiving
   instance's public feed or global search does **not** surface the federated-in non-public content;
   a signed request as the named local recipient **does** see it.

In short: **no suppression on receipt; the read-path filter is the single privacy boundary, and it
applies identically whether the content originated locally or arrived by federation.**

## Why this (and not suppression on receipt)

- **Federation correctness.** Remote instances legitimately receive non-public content addressed to
  their local actors (a DM from alice on instance A to bob on instance B *must* reach B for bob to
  read it). Suppressing it on receipt would drop legitimate messages. The content has to land in
  bob's outbox on B so bob can see it.
- **A single, testable privacy boundary.** Storing the content intact and filtering on the read path
  keeps the privacy rule in exactly one place (`VisibilityFilter`), already exercised by the local
  S5 surface tests. A separate inbound suppression path would be a second, parallel privacy rule that
  could drift from the read-path filter.
- **Audience integrity for the recipient.** The named recipient (bob) sees the content with its
  original `to`/`cc` intact, so the client can render the correct audience (e.g., "direct message to
  bob") rather than a clobbered or stripped audience.
- **Consistency with the local case.** The S5 local read-path filter (139.2-s5) already hides a
  locally-authored DM from non-recipients on the public feed + search. The federation policy extends
  the *same* rule to federated-in content, so there is no distinction in behavior based on origin.

## What was changed

**Test-only slice** — no production source changed. The behavior above is already correct in the
codebase (inbound stores the full document; the S5 read-path filter hides it from non-recipients).
This slice **pins** the decision with cross-instance integration tests so a future refactor (e.g., an
inbound audience rewrite, a store-level suppression, or a change to `CreateActivityHandler`'s
recipient handling) cannot silently regress it.

`tests/Iris.Server.Tests/CrossInstanceVisibilityIntegrationTests.cs` (the two-host A=alice / B=bob
fixture from Phase 136.18):

- **Updated** `DirectPost_FederatedToRemote_StoredOnRemote_CurrentGap` →
  `DirectPost_FederatedToRemote_StoredOnRemote_AudienceIntact`: now also asserts the stored note's
  `to` still names bob (the original recipient) — the audience is preserved, not clobbered by an
  outbound rewrite on the inbound storage path.
- **Updated** `DirectPost_StoredInOutbox_VisibleInPublicFeed_CurrentGap` →
  `DirectPost_StoredInOutbox_HiddenFromPublicFeedAndSearch` (renamed; the body already asserted the
  fixed S5 behavior — the DM is in the author's outbox but hidden from the public feed + search for
  an anonymous requester).
- **Added** 4 new tests pinning the S5(b) read-path policy on the receiving instance (B):
  - `FederatedDm_HiddenFromRemotePublicFeed_ForAnonymous` — a federated-in DM is in bob's outbox on
    B but is **not** in B's public feed for an anonymous request.
  - `FederatedDm_VisibleInRemotePublicFeed_ToNamedRecipient` — the same DM **is** in B's public feed
    when requested by bob (the named local recipient), via `PublicFeedService.GetPublicFeedAsync`
    with `requesterIri: bobIri`.
  - `FederatedDm_NotFoundInRemoteGlobalSearch_ForAnonymous` — the DM is **not** found in B's global
    search for an anonymous request.
  - `FederatedDm_FoundInRemoteGlobalSearch_ByNamedRecipient` — the DM **is** found in B's global
    search when requested by bob, via `GlobalSearchService.SearchAsync` with `requesterIri: bobIri`.

## Test counts

+4 new tests (the 4 S5(b) read-path tests above) + 2 renamed/updated existing tests (test #2 and test
#4, both previously pinned a "known gap" that 139.2-s5 / this change closed).

Verification: full solution build clean (0 warnings / 0 errors, `TreatWarningsAsErrors`); full
`Category!=Slow` suite green — **2,256 tests, 0 failed** across 11 projects (Iris.Server.Tests now
1,361, +4 new tests). The only failures are the 5 pre-existing `Iris.LiveInterop.Tests` Lemmy
peering failures (unrelated to Iris code — a separate track).

## Decisions

- **Store intact, filter on read.** No inbound suppression, no store-level visibility marker, no new
  DB column. The `to`/`cc` audience is the single source of truth; the read-path filter is the
  single privacy boundary.
- **Test-only slice.** The behavior was already correct; this slice pins it so it cannot regress.
  Consistent with the 136.18 / 136.16 / 136.17 test-only integration pattern.
- **No production source change.** No new NuGet packages, no upward dependency violations, no
  migration.
- **The named local recipient can see federated-in non-public content.** This is correct and
  intentional: bob (on B) is the recipient of a DM from alice (on A), so bob must be able to read it
  on B. The privacy guarantee is that *non-recipients* on B (and anonymous visitors) cannot see it.

## Deferred / out of scope

- **Object document for federated-in non-public content** (139.2-s5 deferred surface #2): a
  non-recipient fetching a specific object IRI on B still gets the full object (the object-document
  endpoint does not apply the visibility filter for cross-instance notes — a known limitation
  documented in 136.16). This is a larger design decision (it interacts with federation: remote
  instances legitimately need to fetch non-public content to store/deliver it) and remains deferred.
- **`cc`-to-follower-set model** (139.2-s5 deferred surface #4): whether a followers-only post should
  be visible to *all* followers via a computed follower set (vs. its named `to`/`cc` recipients
  only). A product decision, unchanged by this slice.
