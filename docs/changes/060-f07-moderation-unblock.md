# 060 — F-07 moderation: un-block (`Undo` of `Block`)

> 2026-08-29 · Slice 12.15 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes the **un-block** half of gap **F-07** (moderation): Slices 12.13/12.14 **recorded** the directed
block edge `<c>blocker → blocked</c>` and **applied** it (feed exclusion + delivery suppression), but
there was no way to *reverse* a block — a `Block` was permanent. This slice makes the moderation decision
reversible, per ActivityStreams, by handling the **`Undo` of a `Block`**:

- **Un-block handler.** `UndoActivityHandler` (the F-11 un-follow `Undo` handler) now also handles an
  `Undo` whose object is a `Block`. It resolves the original `Block`'s parties from the local activity
  store (the `Undo`'s object is a reference to the original `Block`, by IRI — the same resolution pattern
  the existing follow branch uses for the original `Follow`) and removes the `<c>blocker → blocked</c>`
  edge from the `IModerationStore` via `RemoveBlockAsync` — the inverse of `BlockActivityHandler`. The
  check is performed *before* the follow branch (a `Block` has no follow target to resolve), and an
  `Undo` of any other activity type (not a `Follow` or a `Block`) remains a no-op.
- **Client.** `IActivityPubClient` / `ActivityPubClient` gained `UnblockAsync(actorId, targetId, ct)` —
  the inverse of `BlockAsync`. It builds an `Undo` (actor = `actorId`, object = a link to the original
  `Block`'s deterministic IRI `{actor}/blocks/{target}`) and delivers it to `targetId.InboxOf()` (the
  previously-blocked actor's inbox, so the receiving instance removes the recorded edge). The `Undo` gets
  its own deterministic, unique-per-`(actor, target)` `Id` so a retried un-block dedupes on the receiver.

The block and un-block are now symmetric writes against the same `IModerationStore`: `RecordBlockAsync`
(Slice 12.13) and `RemoveBlockAsync` (this slice). Slice 12.14's feed/delivery logic reads the *live*
edge set, so a block and its later un-block automatically flip the feed exclusion and delivery
suppression on and off — no extra wiring.

*Scope note:* this slice is the **un-block** (`Undo` of `Block`). It does **not** add `Mute` / `Flag`
(the remaining F-07 moderation verbs; `Flag` is the natural next addition, `Mute` has no ActivityStreams
type and is Iris-specific). F-06 (shared-inbox / relay) is the next Phase 12 item after F-07 is closed.

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/UndoActivityHandler.cs` | Now handles `Undo` of `Block`: resolves the original `Block`'s parties from the activity store and calls `IModerationStore.RemoveBlockAsync` (the inverse of `BlockActivityHandler`). A new `ResolveBlockEdgeAsync` helper + the `BuildUndo` test helper generalized to `Activity`. |
| `src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs` | `UnblockAsync` (signed `Undo` of the `Block` → target inbox, the inverse of `BlockAsync`). |
| `tests/Iris.Server.Tests/UndoActivityHandlerTests.cs` | 5 new unit tests (local-blocker un-block, block-of-local un-block, block-not-stored no-op, unknown-block-IRI no-op, un-block does not touch follow edges). |
| `tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs` | 1 new end-to-end test: block → edge recorded + feed excludes; un-block → edge removed + feed re-includes (over the wire). |
| `tests/…` (3 client stubs) | `FeedServiceTests`, `IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests` each gained a no-op `UnblockAsync` to satisfy the widened interface. |

## Tests

757 → **763** (+6):

- `tests/Iris.Server.Tests/UndoActivityHandlerTests.cs` — 5 new. Each drives the real
  `UndoActivityHandler` against an `InMemoryPersistenceProvider` (moderation + activities + follows).
  Coverage: a **local blocker** undoing its `Block` removes the forward edge (the blocked actor is no
  longer in the blocker's `blocks`); a **block of a local actor** being undone clears the inverse query
  (`GetBlockersAsync` is empty — the local actor is no longer blocked); an `Undo` referencing a
  **never-stored** `Block` is a no-op (the edge cannot be resolved, so it is untouched); an `Undo`
  referencing an **unknown block IRI** is a no-op; and an un-block **does not touch follow edges** (the
  follow edge is intact while the block edge is cleared). The pre-existing 10 follow/un-follow tests are
  unchanged (the `BuildUndo` helper was generalized from `Follow` to `Activity`).
- `tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs` — 1 new end-to-end. Bob follows carol and
  carol has a post; the test asserts (a) `BlockAsync` (202) records the edge and carol's note IRI drops
  out of bob's followed feed, then (b) `UnblockAsync` (202) removes the edge (the `blocks` collection is
  empty again) and carol's note IRI **reappears** in bob's followed feed — the block and its un-block are
  symmetric, end to end over the wire.

## Decisions

- **The un-block resolves the parties from the original `Block`, not from the `Undo`'s actor/object.**
  The `Undo`'s `actor` is the un-blocker (the same party as the original blocker) and its `object` is a
  *reference* to the original `Block` (not the blocked actor). Resolving the original `Block` from the
  activity store (the exact pattern the follow branch uses for the original `Follow`) guarantees the
  removal is scoped to the *precise* edge that was recorded (a local blocker of anyone, or a blocker of a
  local actor), and is robust to a malformed `Undo` (no resolvable `Block` → no-op, nothing removed). This
  mirrors the F-11 un-follow design and keeps the handler's two branches symmetric.
- **The un-block is delivered to the previously-blocked actor's inbox (like the block).** The `Block` was
  delivered to `targetId.InboxOf()` (the blocked actor's inbox, per §5.2.1.3), so the `Undo` goes to the
  same inbox — the receiving instance (the one that recorded the edge, since either party being local
  triggers recording) is the one that removes it. The `Undo` references the original `Block` by its
  deterministic `{actor}/blocks/{target}` IRI (the same IRI `BlockAsync` mints), so the two are
  unambiguously paired.
- **`RemoveBlockAsync` is idempotent-safe (a no-op when absent).** Un-blocking a block that does not exist
  (e.g. a retried un-block, or an un-block with no prior block) simply removes nothing — `RemoveBlockAsync`
  returns `false` and the edge set is unchanged. Combined with the `Undo`'s deterministic `Id` (C-07
  dedupe), a re-delivered un-block is harmless.
- **The un-block clears only the moderation edge, never the follow edge.** A block and a follow are
  independent relationships; un-blocking carol does not un-follow her. The handler's `Undo`-of-`Block`
  branch touches only `IModerationStore`, and the new test `UndoOfBlock_DoesNotTouchFollowEdges` pins that
  boundary. (Whether un-blocking should also *re-include* a previously-suppressed delivery is a delivery
  concern Slice 12.14 already handles automatically — once the edge is gone, the suppression predicate
  `IsBlockedAsync` returns false and delivery resumes.)
