# 058 — F-07 moderation: Block + moderation store + blocks collection

> 2026-08-29 · Slice 12.13 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes the first half of gap **F-07** (moderation — the `Block` activity + the moderation store + the
`blocks` collection): before this slice the instance had **no moderation model at all** — a
`Block` (ActivityPub §5.2.1.3) delivered to an inbox fell through with no handler, no storage, and no
endpoint. The instance now records the directed block edge `<c>blocker → blocked</c>` when **either**
party is a local actor, serves it as a paged `<c>blocks</c>` collection on the actor document, and
exposes the client's one-call `<c>BlockAsync</c>` / `<c>GetBlocksAsync</c>` so a user can block an
actor and read back who they have blocked.

- **Moderation store.** `IModerationStore` / `InMemoryModerationStore` records the directed block edge
  in both directions: a forward map `<c>blocker → {blocked…}</c>` (for a local actor's `blocks`
  collection) and an inverse map `<c>blocked → {blockers…}</c>` (so the instance knows when a local
  actor is *blocked by* someone, e.g. to later suppress outbound delivery to that blocker).
  `RecordBlockAsync` is idempotent (a retried `Block` does not duplicate the edge). `GetBlocksAsync`
  / `GetBlockersAsync` return IRI-sorted snapshots; `IsBlockedAsync` is the directed predicate;
  `RemoveBlockAsync` (an un-block) is present for the follow-up `Undo`-of-`Block` slice. The store is
  wired into `IPersistenceProvider` as `Moderation` (alongside `Likes` / `Follows` / `Replies`).
- **`BlockActivityHandler`.** An `IActivityHandler` for the `Block` type (registered as a singleton
  alongside the other activity handlers). On a `Block` it resolves the actor and object IRIs and, when
  **either** is a local actor (via `ILocalActorResolver`), records the edge in the moderation store.
  A `Block` between two remote actors is a no-op (not this instance's concern), and a malformed
  `Block` (no resolvable actor or object) records nothing.
- **`blocks` collection.** The collection route now accepts `…/blocks` (regex `outbox|followers|
  following|liked|blocks`), and `CollectionEndpointHandler` serves it from
  `persistence.Moderation.GetBlocksAsync` (the blocked actors' IRIs, as links, paged exactly like
  `following` / `liked`). The actor document advertises the `blocks` collection link via
  `ExtensionData` (the ActivityStreams library's `Person` has no typed `blocks` property, the same
  pattern as `feed`).
- **Client.** `IActivityPubClient` / `ActivityPubClient` gained `BlockAsync(actorId, targetId, ct)`
  (builds a `Block` with a deterministic, unique-per-`(actor, target)` `Id` and delivers it to
  `targetId.InboxOf()`, mirroring `FollowAsync`) and `GetBlocksAsync(actorId, query, ct)` (reads the
  actor's `blocks` collection at `actorId.BlocksOf()`, through the `CollectionPageCache`, mirroring
  `GetRepliesAsync`). `Iri.BlocksOf()` was added to `IriExtensions`.

*Scope note:* this slice is the **block edge + the `blocks` collection** — the F-07 "Block" half. It
does **not** yet (a) honor the edge when filtering a local actor's feed or suppressing outbound
delivery to a blocker (the *application* of the moderation decision, deferred to a follow-up slice
along with the `Undo`-of-`Block` un-block), or (b) cover `Mute` / `Flag` (the other F-07 moderation
verbs; `Flag` is the natural next addition, `Mute` has no ActivityStreams type and is Iris-specific).
The `IModerationStore.RemoveBlockAsync` + `GetBlockersAsync` seams are already in place for those.

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/IModerationStore.cs` | The moderation seam (`RecordBlockAsync`, `RemoveBlockAsync`, `GetBlocksAsync`, `IsBlockedAsync`, `GetBlockersAsync`). |
| `src/Iris.Server.InMemory/InMemoryModerationStore.cs` | The default in-memory store (forward + inverse `ConcurrentDictionary<Iri, HashSet<Iri>>`, IRI-sorted snapshots). |
| `src/Iris.Server/BlockActivityHandler.cs` | The `Block` `IActivityHandler`: records the edge when either party is local. |
| `src/Iris.Server/IPersistenceProvider.cs` / `InMemoryPersistenceProvider.cs` | The `Moderation` property (the store is reachable through the aggregate provider). |
| `src/Iris.Server.InMemory/InMemoryPersistenceExtensions.cs` | DI: registers `InMemoryModerationStore` + wires it into the aggregate provider. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | The `blocks` collection route + `CollectionEndpointHandler` case + the actor-document `blocks` advertisement + the `BlockActivityHandler` DI registration. |
| `src/Iris.Core/IriExtensions.cs` | `Iri.BlocksOf()` (`{actor}/blocks`). |
| `src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs` | `BlockAsync` (signed `Block` → target inbox) + `GetBlocksAsync` (reads the `blocks` collection). |
| `tests/Iris.Server.Tests/BlockActivityHandlerTests.cs` | 10 new unit tests (local-blocker, local-blocked, both-remote no-op, idempotent, guards, null-guards). |
| `tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs` | 6 new integration tests (actor-doc advertisement, empty collection, inbound block records the edge, the `blocks` endpoint serves it, two blocks, client read-back). |

## Tests

730 → **746** (+16):

- `tests/Iris.Server.Tests/BlockActivityHandlerTests.cs` — 10 new. Each drives the real
  `BlockActivityHandler` against an `InMemoryPersistenceProvider` + `DefaultLocalActorResolver`.
  Coverage: a local blocker's `Block` records the forward edge (the blocked actor is in the blocker's
  `blocks`); two blocks record two edges; a local blocker of a *local* actor records the edge; a
  repeated `Block` is idempotent (single edge); a remote blocker of a local actor records the edge in
  the inverse query (`GetBlockersAsync`) but leaves the local actor's forward `blocks` empty; a
  block between two remote actors records nothing; a `Block` with no actor or no object records
  nothing; and the constructor's null guards throw `ArgumentNullException`.
- `tests/Iris.Server.Tests/BlocksCollectionIntegrationTests.cs` — 6 new. A single instance
  (b.domain.local) hosts two local actors (bob the blocker, carol the blocked). Coverage: the actor
  document advertises the `blocks` collection link; the `blocks` collection is an empty
  `OrderedCollection` before any block; a signed inbound `Block` (delivered to carol's inbox,
  signature-validated) records the edge; the `blocks` endpoint serves the recorded edge (as a link to
  carol); a second block appends a second item; and the client's `GetBlocksAsync` reads the collection
  back (a plain link item deserializes to a `Link` carrying the blocked actor's IRI).

The three existing `IActivityPubClient` test stubs (`FeedServiceTests`,
`IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests`) each gained no-op
`BlockAsync` / `GetBlocksAsync` members to satisfy the widened interface.

## Decisions

- **A dedicated `IModerationStore`, not `ExtensionData` on the actor document.** Per the F-07 open
  question #3 resolution: moderation relationships (block edges, and later mute/flag) are a distinct
  domain from the actor's identity, so they live in their own store behind an interface (a production
  host swaps in a persistent one) rather than being stashed in the actor document's `ExtensionData`.
  The actor document still *advertises* the `blocks` collection link (via `ExtensionData`) — that is
  presentation, not storage.
- **The edge is recorded when either party is local.** A local actor's block of anyone, *and* anyone's
  block of a local actor, both matter to the instance (the former for the actor's `blocks` collection;
  the latter for knowing the local actor is blocked, enabling delivery suppression in a follow-up
  slice). A block between two remote actors is irrelevant to this instance and is dropped. This keeps
  the store small (only edges touching a local actor) and mirrors the `LikeActivityHandler`'s
  locality guard.
- **Idempotent recording.** `RecordBlockAsync` is a set-union, so a retried `Block` (at-least-once
  delivery, F-22) never duplicates the edge. The client's `BlockAsync` also mints a deterministic,
  unique-per-`(actor, target)` activity `Id`, so the inbox pipeline's `Id`-based dedupe (C-07) catches
  a re-delivered `Block` before it reaches the handler.
- **`blocks` is a stable, paged collection (like `following` / `liked`).** The `blocks` list only
  grows (un-blocks are rare and deferred), so it is served as an `OrderedCollection` of blocked-actor
  links, enumerated through the same `CollectionPageCache` as every other collection — no special
  pagination.
- **`Block` delivery targets the blocked actor's inbox (per §5.2.1.3).** The `Block` is delivered to
  `targetId.InboxOf()` (the blocked actor's inbox), not the blocker's — the receiving instance is the
  one that records the edge. The `Block` is a POST (non-idempotent), so the client's `RetryHandler`
  passes it straight through without retrying (the `DeliveryWorker`'s retry, F-22, is the only retry
  in play).
