# 161g — Outbox single-source-of-truth: full management-operation coverage (19.6.1)

## Summary

Phase 19.6.1 (CI-testable half): extend the `OutboxSingleSourceOfTruthIntegrationTests` to author
**every** supported ActivityStream management operation and verify the outbox contains exactly that
set, each once, in stable (newest-first) order. This pins that every management-style operation is
expressible as a signed ActivityStream message through the outbox — no side channel.

## What changed

### Test (`Iris.Server.Tests/OutboxSingleSourceOfTruthIntegrationTests`)

The `EveryAuthoredActivity_AppearsInTheOutbox_OnceInStableOrder` test now authors 14 activities (was
9):

| # | Activity | Operation |
|---|---|---|
| 1 | `Follow` | follow bob |
| 2 | `Create` | post a note |
| 3 | `Like` | like a note |
| 4 | `Announce` | boost a note |
| 5 | `Block` | block bob |
| 6 | `Undo` (of Follow) | un-follow bob |
| 7 | `Delete` | delete the note |
| 8 | `Accept` | accept a remote follow |
| 9 | `Reject` | reject a remote follow |
| 10 | `Flag` | flag bob (moderation report) |
| 11 | `Undo` (of Flag) | un-flag bob |
| 12 | `Undo` (of Like) | un-like the liked object |
| 13 | `Undo` (of Announce) | un-boost the announced object |
| 14 | `Undo` (of Block) | un-block bob |

A new `BuildFlag` helper builds a `Flag` activity (actor + object as links). The `BuildUndo` helper
already existed and is reused for the four new Undo variants.

## What's out of scope

The local-moderation operations (Mute/Unmute, Relay/Unrelay) are **non-AP** (D4a): they go through
the `/local/v1` tree with Basic-auth, not the outbox. They are not ActivityStream activities and are
not interpreted from federation, so they do not appear in the outbox. They are covered by their own
tests (`MutesCollectionIntegrationTests`, `RelaysCollectionIntegrationTests`,
`CommunityModerationIntegrationTests`).

## Tests

No new test methods — the existing `EveryAuthoredActivity_AppearsInTheOutbox_OnceInStableOrder` was
extended. Full suite green: **1,254 tests, 0 failed**. Build clean (`TreatWarningsAsErrors` on).

## Result

The CI-testable half of 19.6.1 is pinned: every ActivityStream management operation the server
supports (Follow, Create, Like, Announce, Block, Flag, Undo of any, Delete, Accept, Reject) is
expressible as a signed outbox publish and appears in the actor's outbox exactly once, in stable
order. The raw-inspector (UI) half — driving each write screen through the UI and confirming the
rendered signed message — remains a live/UI-verification item.
