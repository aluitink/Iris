# 061 — F-07 moderation: flag (`Flag` + `Undo` of `Flag`)

> 2026-08-29 · Slice 12.16 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes the **flag** half of gap **F-07** (moderation): Slices 12.13/12.14/12.15 added the `Block`
verb (record → apply → un-block) as the moderation prototype. This slice adds the **`Flag`** verb —
the ActivityStreams moderation *report* — end to end, mirroring the block's record/un-block pattern but
**without** the apply half (a flag does not sever a relationship; it is a report a human (or an
auto-moderator) later acts on):

- **Flag store.** `IModerationStore` gained four flag methods, symmetric to the block methods:
  `RecordFlagAsync(flagger, flagged, ct)` (records the directed `flagger → flagged` edge, idempotent),
  `RemoveFlagAsync(flagger, flagged, ct)` (removes it — the un-flag, no-op when absent),
  `GetFlagsAsync(actor, ct)` (the forward flags collection: the actors `actor` flagged, insertion-
  ordered), and `HasFlaggedAsync(flagger, flagged, ct)` (the directed predicate). `InMemoryModerationStore`
  implements them against a forward-only `_flags` index (reusing the same `Add`/`Remove`/`Snapshot`/
  `Contains` helpers as the block index). The `flags` collection is served at `/ap/v1/u/{handle}/flags`
  (an `OrderedCollection` of flagged-actor links) and advertised on the actor document as a `flags`
  extension link — exactly the wire shape the `blocks` collection uses.
- **Flag handler.** A new `FlagActivityHandler` records the `flagger → flagged` edge when *either*
  party is a local actor (a local flagger's `flags` collection lists the flagged actor; a local flagged
  actor is known to have been flagged). It mirrors `BlockActivityHandler`'s "either party local" rule
  and its no-op guards (a flag with no resolvable actor/object, a flag between two remote actors). It is
  registered in the `IActivityHandler` DI collection. Unlike `Block`, there is **no feed/delivery
  application** — a flag is a report, not a block, so it does not exclude the flagged actor's content or
  suppress delivery (that is `Block`'s job, Slice 12.14).
- **Un-flag handler.** `UndoActivityHandler` now also handles an `Undo` whose object is a `Flag`: it
  resolves the original `Flag`'s parties from the local activity store (the `Undo`'s object is a
  reference to the original `Flag`, by IRI — the same resolution the `Undo`-of-`Block` branch uses) and
  removes the `flagger → flagged` edge via `RemoveFlagAsync`. A new `ResolveFlagEdgeAsync` helper mirrors
  `ResolveBlockEdgeAsync`. An `Undo` of any other activity type (not a `Follow`, a `Block`, or a `Flag`)
  remains a no-op.
- **Client.** `IActivityPubClient` / `ActivityPubClient` gained:
  - `FlagAsync(actorId, targetId, ct)` — builds a `Flag` (actor = `actorId`, object = `targetId`) and
    delivers it to `targetId.InboxOf()` (the flagged actor's inbox), signed by the pipeline. The `Flag`
    gets a deterministic, unique-per-`(actor, target)` IRI `{actor}/flags/{target}` so a retried flag
    dedupes on the receiver.
  - `UnflagAsync(actorId, targetId, ct)` — the inverse of `FlagAsync`: builds an `Undo` (actor =
    `actorId`, object = a link to the original `Flag`'s IRI `{actor}/flags/{target}`) and delivers it to
    the same inbox, removing the recorded edge.
  - `GetFlagsAsync(actorId, query, ct)` — enumerates the actor's `flags` collection (read through the
    `CollectionPageCache`, the same enumeration/caching semantics as `GetBlocksAsync`).

The flag and un-flag are now symmetric writes against the same `IModerationStore`: `RecordFlagAsync` and
`RemoveFlagAsync`, mirroring the block's `RecordBlockAsync`/`RemoveBlockAsync`.

*Scope note:* this slice is the **flag** (`Flag` + `Undo` of `Flag`). It does **not** add `Mute` (the
remaining F-07 moderation verb; `Mute` has no ActivityStreams type and is Iris-specific — it will be
handled as a typed Iris extension, not a 3rd-party activity type). F-06 (shared-inbox / relay) is the
next Phase 12 item after F-07 is closed.

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/IModerationStore.cs` | Four new flag methods (`RecordFlagAsync`, `RemoveFlagAsync`, `GetFlagsAsync`, `HasFlaggedAsync`), symmetric to the block methods. |
| `src/Iris.Server.InMemory/InMemoryModerationStore.cs` | `_flags` forward-only index + the four flag method impls (reuses the block index's `Add`/`Remove`/`Snapshot`/`Contains` helpers). |
| `src/Iris.Server/FlagActivityHandler.cs` (NEW) | Records the `flagger → flagged` edge when either party is local; no-op guards; mirrors `BlockActivityHandler`. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | `FlagActivityHandler` registered in the `IActivityHandler` DI collection; the collection route regex widened to include `flags`; a `flags` handler case (`GetFlagsAsync`); the actor document advertises the `flags` extension link. |
| `src/Iris.Server/UndoActivityHandler.cs` | Now handles `Undo` of `Flag` (a new `ResolveFlagEdgeAsync` helper + an un-flag branch calling `RemoveFlagAsync`), mirroring the `Undo`-of-`Block` branch. |
| `src/Iris.Core/IriExtensions.cs` | `FlagsOf()` (derives `{actor}/flags`), mirroring `BlocksOf()`. |
| `src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs` | `FlagAsync` (signed `Flag` → target inbox, deterministic IRI), `UnflagAsync` (signed `Undo` of the `Flag` → target inbox), `GetFlagsAsync` (enumerates the `flags` collection). |
| `tests/Iris.Server.Tests/FlagActivityHandlerTests.cs` (NEW) | 10 unit tests (local flagger, two flags, local-of-local, idempotent, local-flagged, both-remote no-op, no-actor/no-object guards, null ctor guards). |
| `tests/Iris.Server.Tests/UndoActivityHandlerTests.cs` | 5 new unit tests (local-flagger un-flag, flag-of-local un-flag, flag-not-stored no-op, unknown-flag-IRI no-op, un-flag does not touch block edges). |
| `tests/Iris.Server.Tests/FlagsCollectionIntegrationTests.cs` (NEW) | 6 end-to-end tests (actor doc advertises `flags`, empty collection, inbound flag records edge, flag in the flagger's collection, client `GetFlagsAsync` reads back, un-flag removes edge, flag does **not** exclude from feed). |
| `tests/…` (3 client stubs) | `FeedServiceTests`, `IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests` each gained no-op `FlagAsync`/`UnflagAsync`/`GetFlagsAsync` to satisfy the widened interface. |

## Tests

763 → **785** (+22):

- `tests/Iris.Server.Tests/FlagActivityHandlerTests.cs` — 10 new. Each drives the real
  `FlagActivityHandler` against an `InMemoryPersistenceProvider`. Coverage: a **local flagger** records
  the forward edge (the flagged actor is in the flagger's `flags`); a flagger with **two flags** lists
  both; a local flagger of a **local actor** records the edge; a **repeated** flag is idempotent (no
  duplicate edge); a **local flagged** actor (remote flagger) records the edge (visible via the directed
  predicate `HasFlaggedAsync`, the forward `flags` collection stays empty); a flag **between two remote
  actors** records nothing; a flag with **no actor** / **no object** records nothing; and the two null
  ctor guards.
- `tests/Iris.Server.Tests/UndoActivityHandlerTests.cs` — 5 new. Each drives the real
  `UndoActivityHandler` against an `InMemoryPersistenceProvider` (moderation + activities). Coverage: a
  **local flagger** undoing its `Flag` removes the forward edge; a **flag of a local actor** being undone
  clears the directed edge; an `Undo` referencing a **never-stored** `Flag` is a no-op; an `Undo`
  referencing an **unknown flag IRI** is a no-op; and an un-flag **does not touch block edges** (the block
  edge is intact while the flag edge is cleared). The pre-existing follow/un-follow and block/un-block
  tests are unchanged.
- `tests/Iris.Server.Tests/FlagsCollectionIntegrationTests.cs` — 6 new end-to-end (single instance, bob =
  flagger, carol = flagged local). Coverage: the actor document **advertises** the `flags` collection
  link; the `flags` collection is an empty `OrderedCollection` before any flag; an inbound signed
  `FlagAsync` (202) **records** the edge and carol appears in bob's `flags`; bob's own `/flags`
  collection serves the recorded edge as a link; the client's `GetFlagsAsync` **reads back** the flagged
  actor's IRI over the wire; `UnflagAsync` (202) **removes** the edge (the `flags` collection is empty
  again); and a `Flag` **does not exclude** the flagged actor's content from the flagger's followed feed
  (a flag is a report, not a block — the feed still contains the flagged note IRI).

## Decisions

- **A flag is a report, not a block — no apply half.** The decisive design difference from `Block`
  (Slice 12.13/12.14) is that a `Flag` does **not** sever the relationship. Recording the edge and
  serving the `flags` collection is the whole of the wire-visible behavior; there is no feed exclusion,
  no delivery suppression, no outbox pruning. This is the ActivityStreams semantics: a flag is a
  moderation signal for a human (or auto-moderator) to act on, not an automatic ban. The integration test
  `InboundFlag_DoesNotExcludeFromFeed` pins this boundary (the flagged actor's content stays in the
  flagger's feed after a flag, whereas a block — Slice 12.14 — would have excluded it). This keeps the
  flag's store/handler/client surface minimal and identical in *shape* to the block's, while making the
  *application* explicitly absent.
- **The "either party local" recording rule is shared with `Block`.** A flag is recorded when the
  flagger is local (the local actor's `flags` collection must list it) **or** the flagged actor is local
  (the local instance must know it was flagged — a moderation signal about one of its own actors). This
  is the same rule as the block and keeps `FlagActivityHandler` a near-clone of `BlockActivityHandler`
  (only the store methods and the activity type differ).
- **The un-flag resolves the parties from the original `Flag`, not from the `Undo`'s actor/object.**
  Identical rationale to the un-block (Slice 12.15) and the un-follow (F-11): the `Undo`'s `object` is a
  *reference* to the original `Flag` (by IRI), so resolving the original `Flag` from the activity store
  guarantees the removal is scoped to the *precise* edge that was recorded, and is robust to a malformed
  `Undo` (no resolvable `Flag` → no-op, nothing removed). `ResolveFlagEdgeAsync` is a sibling of
  `ResolveBlockEdgeAsync` (the only differences are the activity type cast and the tuple field names).
- **The flag/un-flag IRI scheme mirrors the block's.** `FlagAsync` mints `{actor}/flags/{target}` (the
  `Flag`'s `Id`), and `UnflagAsync` references that same IRI (the `Undo`'s `object`) while giving the
  `Undo` its own deterministic `{actor}/unflags/{target}` `Id`. This keeps the flag/un-flag pair
  unambiguously linked and dedupe-friendly (C-07), exactly as the block/un-block pair is.
- **The `flags` collection reuses the `blocks` wire shape and store helpers.** The `flags` endpoint is
  served by the same paged-collection endpoint as `blocks` (the route regex is widened, a new handler
  case calls `GetFlagsAsync`, and the actor document advertises a `flags` extension link). The in-memory
  store reuses the block index's `Add`/`Remove`/`Snapshot`/`Contains` helpers for a second `_flags`
  dictionary. This maximizes code reuse and keeps the flag a first-class, wire-visible moderation
  collection (a client — or another instance — can read `{actor}/flags` to enumerate the reports
  against/for that actor).
