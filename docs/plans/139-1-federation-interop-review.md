# 139.1 — Federation & interop conformance review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: verify every activity type Iris
> speaks round-trips correctly against every peer platform it claims to interop with, using real
> live instances where possible (per Phase 138's Lemmy stack + the Phase 19/136 Mastodon test
> accounts), not just `TestServer` fixtures. Reuses Phase 136's cross-instance integration suite and
> Phase 138's interop matrix as the baseline — this review's job is to find what's *not* covered by
> either.

## Peers in scope

| Peer | Local fixture | Notes |
|---|---|---|
| Iris ↔ Iris | two-instance `TestServer` harness + the dev docker stack | baseline; should be fully green already |
| Iris ↔ Lemmy | [lemmy/](../../lemmy/) local stack | per Phase 138 |
| Iris ↔ Mastodon | `@RayvenMX@mastodon.world` (Phase 138 test-account) | real external instance — read-only interop only, do not spam-post |
| Iris ↔ Pleroma | spot-checked in Phase 79.3/81.2 | verify prior findings still hold |
| Iris ↔ Misskey | spot-checked in Phase 81.2 | verify prior findings still hold |
| Iris ↔ PeerTube | spot-checked in Phase 79.3 | verify prior findings still hold; video rendering path |

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Cold WebFinger resolution for every peer type | Resolve `acct:` for a Person and (where applicable) a Group/Community on each peer from a clean Iris instance | Each resolves to the correct actor type without manual `!`-stripping surprises (Lemmy) or host mismatches | curl/UI transcript per peer |
| 2 | Actor document round-trip | Fetch each peer's actor doc via Iris's proxy; confirm all core fields (`inbox`, `outbox`, `publicKey`, `icon`) render in `ActorProfile` | No blank/missing fields beyond documented platform limitations (e.g. Lemmy Person has no `name`/`icon`) | screenshot per peer |
| 3 | Inbound Create (post) rendering | A post/status from each peer type appears correctly in the Iris feed/community view (title where applicable, content, attachments, sensitivity) | Content renders with no raw-JSON fallback, no truncation | screenshot per peer |
| 4 | Inbound reply/comment threading | A reply from each peer type threads correctly under its parent | Correct nesting depth and order (Phase 054 contract) | screenshot |
| 5 | Inbound Like/boost-equivalent | A like (and, for Lemmy, a dislike per Phase 138.17) from each peer is recorded and reflected in counts | `iris:likedCount`/`iris:dislikedCount` update correctly | before/after count diff |
| 6 | Outbound Create delivery | Iris posts to a community/actor followed by each peer type; confirm delivery succeeds and renders on the peer side | 2xx delivery, correct rendering on the peer (where the peer is locally controlled — Lemmy/Iris only; do not push test content to real Mastodon accounts) | delivery log + peer-side screenshot |
| 7 | Outbound Update/Delete propagation | Edit and then delete an Iris post that was delivered to a peer; confirm both propagate | Peer reflects the edit; peer shows the post removed/tombstoned per its own model | peer-side screenshot before/after |
| 8 | Follow/Undo-follow across peer types | Follow and unfollow each peer type from Iris and vice versa (where the peer UI allows it) | Follow/unfollow edges converge correctly on both sides (Phase 145 contract) | collection dump before/after |
| 9 | Pagination/backfill across peer types | Walk a peer's outbox with 50+ items; confirm Iris pages through all of them without loss or duplication | Item count matches source; no duplicate IRIs | count comparison |
| 10 | Signature/header conformance matrix | Capture the exact signature header set each peer sends and requires, compare against Iris's validator/signer (extends Phase 136.3's canonical verification matrix) | No peer's real traffic is rejected/rejects Iris for a header-construction reason not already known | header dump per peer |
| 11 | `@context`/vocabulary sniffing robustness | Confirm Iris's capability detection (`IsLemmy()`-style checks, Phase 137.2) doesn't misfire against Pleroma/Misskey/PeerTube documents | Correct feed/members IRI resolution for every peer type | unit test or live capture |
| 12 | Relay fan-out interop | Confirm a relay-subscribed peer receives fan-out correctly (Phase 28) against a real (or realistically simulated) external relay subscriber | Fan-out delivered, no duplicate/missing activities | delivery log |

## Deliverable check

All 12 scenarios executed with evidence recorded; findings triaged (class + severity) into this
doc's own tracker table (add one, matching the Loop protocol's shared-tracker shape, when scenarios
start failing); [docs/reference/](../reference/) interop matrix (Phase 138.28) updated with anything
new learned here.

## Progress tracking

- [ ] 1  - [ ] 2  - [ ] 3  - [ ] 4  - [ ] 5  - [ ] 6
- [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11 - [ ] 12

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** none started yet — begin at scenario 1.
