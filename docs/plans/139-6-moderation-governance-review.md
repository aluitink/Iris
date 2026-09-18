# 139.6 — Moderation & governance review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: every moderation primitive (block,
> mute, flag/report, community moderation, admin tools) exercised end to end, including cross-instance
> propagation and the new Lemmy-specific moderation semantics from Phase 138 (removal vs. deletion,
> community bans). Builds on Phase 058-062 (F07 moderation), Phase 136.10 (moderation trust boundary),
> Phase 51.4/61.3 (moderation queue, notification filtering), and Phase 84/88 (role management).

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Block a local actor | Block another local actor; confirm their content disappears from feed/search/notifications for the blocker | Content hidden, block reversible (unblock restores visibility) | before/after screenshot |
| 2 | Block a remote actor + propagation | Block a remote actor; confirm the block is enforced locally and (where applicable) propagated (Undo/Block per Phase 231) | Content hidden; propagation activity delivered correctly | delivery log |
| 3 | Mute (local + cross-instance) | Mute a local and a remote actor; confirm muted content is suppressed but not fully blocked (still followable/visible if navigated to directly, per the documented mute semantics) | Correct suppression scope | screenshot |
| 4 | Flag/report a post | Report a post as a non-admin user; confirm it reaches the moderation queue (Phase 51.4) with correct context | Queue entry created, reporter identity handled per policy | screenshot |
| 5 | Community moderation (remove member, reject join) | As a community moderator, remove a member and reject a join request | Correct state change, correct notification to the affected user | screenshot |
| 6 | Admin role management | Grant/revoke an admin role; confirm the new admin gains access and the revoked one loses it immediately | Access changes take effect without requiring re-login (or documented if it does) | screenshot |
| 7 | Cross-instance moderation undo | Un-block/un-mute across instances; confirm the Undo propagates and content reappears | Content restored on both sides | before/after screenshot |
| 8 | Lemmy community ban interop (Phase 138 follow-on) | Where a Lemmy community bans a user (`Block` activity per Phase 138's research notes), confirm Iris records/reflects it sensibly for a synced Lemmy community | Correct, documented behavior — even if the behavior is "not actionable from Iris, display-only" | screenshot + decision note |
| 9 | Lemmy removal vs. deletion in the moderation UI | Confirm a Lemmy moderator's content removal (Phase 138.23) is distinguishable in Iris's UI from an author's own deletion | Distinct, correct UI treatment | screenshot |
| 10 | Moderation notification correctness | Every moderation action that should notify the affected user does, with a friendly label (Phase 121.1) | Notification appears with correct, non-confusing text | screenshot |
| 11 | Abuse edge case — repeated re-follow after block | A blocked actor attempts to re-follow; confirm the block still applies | Follow request rejected/ignored per policy | delivery log |

## Deliverable check

All 11 scenarios executed with evidence; scenarios 8–9 (Lemmy-specific) produce explicit decisions
recorded here (or cross-linked to [Phase 138](phase-138-lemmy-community-integration.md) if the
decision belongs there instead).

## Progress tracking

- [ ] 1  - [ ] 2  - [ ] 3  - [ ] 4  - [ ] 5  - [ ] 6
- [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** none started yet — begin at scenario 1.
