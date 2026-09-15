# 138.29 — Phase 138 closeout

**Date:** 2026-09-15
**Slice:** [docs/plans/phase-138-lemmy-community-integration.md](../plans/phase-138-lemmy-community-integration.md) 138.29 (Stage H, final slice)
**Type:** Closeout (full regression pass + final live manual Playwright pass)

## Objective

Full regression pass (`dotnet test --filter "Category!=Slow"` then the full suite) + a final live
manual Playwright pass across an Iris↔Iris scenario and an Iris↔Lemmy scenario side by side,
confirming no regression to existing Mastodon-facing behavior.

## Regression pass

- **Build:** `dotnet build -c Release` → 0 warnings, 0 errors.
- **Fast** (`--filter "Category!=Slow"`): **1278 passed, 2 failed, 1 skipped**. The 2 failures are
  the **known-flaky** background-delivery tests
  (`MutualPeeringHandshakeIntegrationTests.MutualFollow_BothFollowersCollections_ListTheOther`,
  `FollowEdgeConvergenceIntegrationTests.Follow_Unfollow_Refollow_Cycle_..._StableCollections`) —
  both pass in isolation (4/4).
- **Full** (no filter): **1282 passed, 16 skipped (Slow), 1 failed**. The 1 failure is again the
  known-flaky `MutualPeeringHandshakeIntegrationTests.MutualFollow_...` — passes in isolation (3/3).

**Verdict:** suite green. The only failures across both runs are the two documented known-flaky
background-delivery tests, both of which pass in isolation (the residual thread-pool scheduling
flake, tracked as a next-work-item in PLAN.md's "Known flake"). No real regression.

## Live manual Playwright pass (MCP, clean entry, caching disabled)

Signed in as `andrew` / `Password1`. Two scenarios, side by side:

### Scenario 1 — Iris↔Iris / Mastodon-style peer

- **Signed-out home (`/`):** public timeline renders cross-platform content — Iris posts (alice's
  138.11 fidelity checks, interop fixture posts/replies), Lemmy cross-posts, and Mastodon/Pleroma
  content (Gargron, nomdeb, aharoni, GossiTheDog, devopscats, QasimRashid, …) with **boosts**
  ("Boosted by Gargron"), **replies** ("in reply to"), and **Like/Boost/Reply** controls.
  **0 console errors.**
- **Signed-in home (`/home`):** after a Refresh, the timeline populates with `andrew`'s followed
  content — a stream of **cross-instance Mastodon boosts** (Gargron reblogging devopscats,
  skinnylatte, GossiTheDog, glassbottommeg, QasimRashid, jon, maxleibman, elfin, Tattooed_Mummy,
  douwe), with notes, media, and hashtags. **No Iris-app console errors.** The 24 console errors
  are all **upstream remote-federation fetch failures** (401/404/500 on
  `/ap/v1/proxy/https://…` and remote actor derefs) — the local test instance trying to pull live
  open-Fediverse content it can't fully reach. Expected environment limitation, not an Iris defect.
- **Communities (`/communities`):** lists both **local Iris** communities (`interopX`,
  `owner-test-5428`, `piefed-test`, `technology`, `test-882`, `test-community-541`) and **remote
  Lemmy** communities (`Iris Interop Test Community`, `Iris Interop`, `Technology`). Cross-platform
  community discovery works.
- **Local `interopX` community (`iris.luit.ink/ap/v1/c/interop`):** renders clean (0 errors) with
  Follow/Join, member count, "Post to this community", and community search. The feed shows
  "No posts in this community yet" / "0 members" — **expected**: the community feed aggregates
  *members'* outboxes (138.20 `CommunityFeedService.GetFeedAsync`), and the 138.3 fixture posts are
  on the author's outbox + public timeline, not a member's outbox. Not a regression; the model is
  working as designed.

### Scenario 2 — Iris↔Lemmy

- **Lemmy `Iris Interop` community (`lemmy.luit.ink/c/interop`):** renders with **0 console
  errors**, an active **Unfollow** button (the peering edge is live), Join, "Post to this
  community", Feed/Members tabs, and a **populated feed**:
  - alice's **138.11 fidelity post** (Iris→Lemmy cross-post) — "Boosted by interop" (the Lemmy
    community relay `Announce`). Renders the **`EngagementBar`** (heart 0 / boost 0 / reply).
  - lemmyadmin's **"138.3 fixture post two"** — renders the **`LemmyVoteBar`** (↑ 1 upvote,
    ↓ 0 downvote, **2 comments**).
  - lemmyadmin's **"Hello from Lemmy interop"** — also `LemmyVoteBar` (↑ 1).
- **Platform-conditional UI validated (138.27 F6):** the same feed, the same `ObjectView`, but the
  vote-bar-vs-engagement-bar gate correctly switches on platform — Lemmy-sourced content gets the
  `LemmyVoteBar`, Iris-authored content gets the `EngagementBar`. This is the IRI-based gate working
  exactly as designed for today's platforms, and confirms the S2 follow-up (generalize the gate to
  capability-driven) is the right future work.
- **Iris→Lemmy leg is working live:** the cross-post delivered and the relay is visible — K1
  (Lemmy-side signature parse) is **not** currently blocking the Iris→Lemmy direction in this
  environment. A positive closeout signal beyond the documented expected-blocked bar.

**Evidence:** auto-saved screenshots `tmp/.playwright-mcp/page-2026-09-15T23-01-32-785Z.png`
(Lemmy community header + first feed item) and `tmp/.playwright-mcp/page-2026-09-15T23-01-52-818Z.png`
(the platform-conditional UI: `EngagementBar` on alice's post, `LemmyVoteBar` on lemmyadmin's posts).

## Findings logged (no new defects; two pre-existing follow-ups reconfirmed)

- **F-1 (expected, not a defect):** local `interopX` community feed is empty (0 members) — the
  member-outbox-merge feed model only shows members' content. The 138.3 fixtures are author-outbox
  posts. Documented as the model working as designed; no action.
- **F-2 (environment, not a defect):** upstream remote-federation fetch failures (401/404/500) on
  the signed-in home — the local test instance can't fully reach the open Fediverse. Expected for a
  dev instance; no Iris-app error.
- **Pre-existing follow-ups reconfirmed (from 138.27):** S2 (generalize the vote-bar gate to
  capability-driven; rename `LemmyVoteBar` → `VoteBar`) and S3 (cosmetic `IsLemmy` rename) remain
  open for a later UX slice. The live pass confirms the current IRI-based gate is producing correct
  behavior, so neither is blocking.

## Phase 138 status

**All 29 slices (138.1–138.29) complete.** Stage H (cross-platform consistency review) closed:
138.27 audit, 138.28 conformance matrix, 138.29 closeout. Phase 138 (Lemmy community integration)
is **DONE**. Next: **Phase 139** (whole-platform end-to-end review).

## Check

- [x] Full suite green (fast + full; only known-flaky background-delivery tests fail, pass in
  isolation).
- [x] Manual pass findings logged (this doc + the two reconfirmed 138.27 follow-ups).
- [x] PLAN.md's Recently Completed updated.
- [x] ROADMAP.md entry added (Stage H line).
