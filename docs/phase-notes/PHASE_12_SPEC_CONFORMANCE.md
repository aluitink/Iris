# Phase 12 — Spec conformance and missing-feature closure

> Extracted from the historical changelog. The changelog now keeps a short pointer to this phase note.

## Overview

Phase 12 shifted the project from feature completion to correctness and compatibility. It included:

- a missing-feature audit against the ActivityPub / ActivityStreams / RFC 8615 / NodeInfo surface
- a severity-ranked fix plan
- a conformance suite to keep the server honest
- the last major feature gaps for Wave 1 and first Wave 2 items

## Slice 12.1 — Missing-feature inventory + spec audit

- Recorded the severity-ranked gap list in `docs/reference/MISSING_FEATURES.md`.
- This was a research-only pass; it did not change production code.
- It covered spec expectations for identity, routing, object updates/deletes, shared inbox, Like collection, and follow-feed surfaces.

## Slice 12.2 — Shared inbox

- Added instance-level `SharedInboxIri` support.
- The server now advertises `endpoints.sharedInbox` when configured.
- Delivery resolves an advertised shared inbox before falling back to `recipientIri.InboxOf()`.
- This closes the Wave 1 blocker item for shared-delivery semantics.

## Slice 12.3 — Update/Delete and object endpoint

- Added `UpdateActivityHandler` to refresh stored object content in place.
- Added `DeleteActivityHandler` to tombstone deleted objects instead of returning a `404`.
- Added `GET /ap/v1/o/{**path}` to serve stored objects and tombstones.
- This resolves the object-write path and object endpoint semantics.

## Slice 12.4 — Ed25519 support

- Unified signing around `ISigningKey`.
- Added a BouncyCastle-backed `Ed25519Key` implementation.
- Accepted Ed25519 keys inbound and signed outbound with them.
- This closes the major interop gap for servers like Pleroma that use Ed25519.

## Slice 12.5 — Move handler

- Added `MoveActivityHandler`.
- When an actor moves to a new IRI, local follow edges are repointed to the new IRI.
- The handler also invalidates stale key and actor-document cache entries.
- This closes the final Wave 1 `Move` gap.

## Slice 12.6 — Conformance tests + RFC 8615 fix

- Added regression tests for:
  - WebFinger at the RFC root path and the correct media type (`application/jrd+json`)
  - NodeInfo 2.0 structure
  - actor document content type and fields
  - shared inbox advertisement
  - outbound `ServerToServer` signature shape (`digest` + `content-type`)
- Fixed WebFinger to serve `application/jrd+json` instead of `application/json`.

## Slice 12.7 — Like + liked collection

- Added `ILikeStore` and `InMemoryLikeStore`.
- Added `LikeActivityHandler` for both local liker recording and community feed propagation.
- Added the `liked` collection endpoint and public docs advertisement.
- This closes the first Wave 2 feature gap.

## Slice 12.8 — Followed feed / home timeline

- Added the home timeline service that merges followed actors’ outboxes.
- It computes a deterministic, de-duped, capped feed from local + remote follows.
- This closes the most important user-facing feed feature after the basic read path.

## Slice 12.9 — F-12 interpret `inReplyTo` / `tag` / `attachment`

- Added the `IReplyStore` (parent IRI → reply IRIs) + `InMemoryReplyStore`.
- The `CreateActivityHandler` records the parent → child reply edge from `inReplyTo`; the
  parent's replies are served as a paged `OrderedCollection` at `GET {object-iri}/replies`
  (dispatched inside the object-document handler).
- The object-document route moved from `/o/{**path}` to `{**path}` so the object IRI is the
  endpoint IRI (matching federation behavior).
- Client-side: `IActivityPubClient.PostReplyAsync` / `GetRepliesAsync`.

## Slice 12.10 — Federated Update / Delete propagation (F-02/F-03 federated half + F-12 reply-edge cleanup)

- Added `IDeletePropagationService` / `DeletePropagationService`: the single owner of the
  remote fan-out for an object `Update` / `Delete`. For an `Update` the targets are the
  author's **remote followers**; for a `Delete` they are the author's remote followers **plus**
  the **remote parent's owner** when the deleted object is a reply (the parent's instance holds
  the replies collection, F-12). Local targets are skipped (their copy is refreshed/tombstoned
  locally). Each target is delivered via `IDeliveryService.DeliverToActorAsync` (signed as the
  author; per-actor inbox / sharedInbox resolution).
- `UpdateActivityHandler` / `DeleteActivityHandler` now **accept a federated activity from a
  remote owner** when this instance holds a copy of the object (stored via the outbound
  `Create` federation, Slice 11.7) and the stored object is attributed to that actor
  (relaxed the local-only owner guard). A remote instance holding a copy now applies the
  author's later `Update` / `Delete` to it.
- Only the author's **home** instance (where the actor is local) re-propagates: a remote
  instance that already received the activity does not fan it out again (and does not own the
  author's follower set).
- The `DeleteActivityHandler` captures the deleted object's **parent object before
  tombstoning** (a `Tombstone` carries no `inReplyTo`) so the propagation can resolve the
  remote parent's owner (its `attributedTo`); it also removes the local parent → child reply
  edge for a deleted reply (F-12).
- `ObjectPropagationIntegrationTests` (3 tests) covers the two-instance E2E flow: author A
  creates a post → remote C receives it via federation; A edits → C's copy is refreshed; A
  deletes → C's copy is tombstoned.

## Slice 12.11 — F-13 global search (instance-wide actor + content directory)

- Added `IGlobalSearchService` / `GlobalSearchService`: a case-insensitive substring search over
  the instance's **own** local surface — the local actors (the directory, matched by `name` /
  `preferredUsername` / IRI) and the stored content objects (matched by `content` / `name`).
  `Tombstone`s are skipped and an object that is an actor is matched only by the actor pass (not
  duplicated as content). An empty/whitespace query matches everything (the endpoint doubles as an
  unfiltered directory / listing). Results are ordered deterministically: actors first, then content
  objects, each sub-list IRI-sorted (ordinal).
- `GET /ap/v1/search` serves the result as a paged `OrderedCollection` reusing the shared
  `BuildSearchPageDocument` (page 1 `OrderedCollection` + `next`, page 2+ `OrderedCollectionPage` +
  `prev`/`next`; the query term recorded under `iris:searchQuery`). Computed fresh per request (not
  the local collection-page cache), like the community search. Registered in DI via
  `TryAddSingleton<IGlobalSearchService, GlobalSearchService>`.
- `IActorStore.ListActorsAsync` / `IObjectStore.ListObjectsAsync` (in-memory impls) are the new read
  surface the search enumerates.
- Client: `IActivityPubClient.SearchAsync(instanceBase, query, SearchOptions)` requests a single
  page (up to `Limit`, default 100, at `Offset`) from `{base}/search` (derived by `Iri.SearchOf`)
  and yields its items; the response is not cached.
- *Scope note:* instance-local only — a cross-instance (relay / WebFinger) search is a separate,
  larger feature (out of scope for F-13, matching the per-community search).
 - `GlobalSearchIntegrationTests` (9) covers the live endpoint: actor + content matching,
   case-insensitivity, the directory listing, `limit`/`offset` paging (page 1 `next` / page 2 `prev`
   no `next` / offset-past-end), and a client `SearchAsync` round-trip. `GlobalSearchServiceTests`
   (5) covers the service in isolation (ordering, the matching surfaces, no-match, and the
   tombstone / content-pass-actor exclusions).

 ## Slice 12.12 — F-22 delivery retry / dead-letter (at-least-once delivery)

 - Added `DeliveryRetryOptions` (`MaxAttempts`=5, `BaseDelay`=1s, `MaxDelay`=60s) — the worker's retry
   policy; a host may rebind it to tune the retry budget (`MaxAttempts`=1 for fail-fast).
 - `DeliveryJob` gained an `Attempts` counter (default 0) + `AfterAttempt()` so a retry can track how
   many times a job has been tried.
 - Added `IDeliveryDeadLetterStore` / `InMemoryDeliveryDeadLetterStore` (bounded, newest-first, the
   oldest is evicted beyond `capacity`=1000) + `DeadLetterEntry` (inbox, activity, actor, attempts,
   `DeadLetterFailureKind`, last failure detail, timestamp; `ToJob()` re-drives it). A production host
   swaps in a persistent `IDeliveryDeadLetterStore`; the worker depends only on the interface.
 - The `DeliveryWorker` now retries a failed delivery up to `MaxAttempts` total attempts with
   exponential backoff (`BaseDelay` doubled per retry, capped at `MaxDelay`) so a downed peer is not
   hammered; on a 2xx it is done. When the budget is exhausted the job is moved to the dead-letter
   store (or logged at `Error` + dropped when no store is configured, preserving the pre-F-22 opt-out).
   A delivery failure never throws out of the worker. This is *at-least-once for failed attempts* — a
   re-delivered activity is deduped by its `Id` on the receiver (C-07), so a retry is a harmless no-op.
 - The worker is registered via an explicit DI factory (not `AddHostedService<DeliveryWorker>()`) so the
   retry policy + dead-letter store are injected deterministically (two-constructor overload would
   otherwise rely on most-constructible overload selection).
 - `DeliveryRetryTests` (8): a successful delivery is delivered on the first attempt (no retry); a
   transient failure is retried until success (not dead-lettered); a permanent failure exhausts the
   budget and is dead-lettered with the correct attempt count + failure kind + status; a transport
   error is dead-lettered as `TransportError`; `MaxAttempts`=1 is fail-fast but still dead-letters;
   without a store the exhausted job is dropped (not a crash); the dead-letter store evicts the oldest
   beyond capacity; and the backoff delay grows exponentially and is capped (unit-tested via the
   worker's private `BackoffDelay`).

 ## Slice 12.13 — F-07 moderation: `Block` + moderation store + `blocks` collection

 - Added `IModerationStore` / `InMemoryModerationStore`: records the directed block edge
   `blocker → blocked` in **both** directions — a forward map (for a local actor's `blocks`
   collection) and an inverse map (so the instance knows when a local actor is *blocked by* someone,
   enabling delivery suppression in a follow-up slice). `RecordBlockAsync` is idempotent (a retried
   `Block` never duplicates the edge); `GetBlocksAsync` / `GetBlockersAsync` return IRI-sorted
   snapshots; `IsBlockedAsync` is the directed predicate; `RemoveBlockAsync` (an un-block) is present
   for the follow-up `Undo`-of-`Block` slice. Wired into `IPersistenceProvider` as `Moderation`.
 - Added `BlockActivityHandler` (`ActivityHandlerBase<Block>`, registered as a singleton): on a `Block`
   it resolves the actor + object IRIs and, when **either** is a local actor (via
   `ILocalActorResolver`), records the edge. A block between two remote actors is a no-op; a malformed
   `Block` (no resolvable actor or object) records nothing.
 - The `blocks` collection: the collection route now accepts `…/blocks`; `CollectionEndpointHandler`
   serves it from `persistence.Moderation.GetBlocksAsync` (the blocked actors' IRIs as links, paged
   like `following` / `liked`); the actor document advertises the `blocks` link via `ExtensionData`
   (the library's `Person` has no typed `blocks` property, the same pattern as `feed`).
 - Client: `IActivityPubClient.BlockAsync(actorId, targetId)` builds a `Block` (deterministic,
   unique-per-`(actor, target)` `Id`) and delivers it to `targetId.InboxOf()` (mirroring
   `FollowAsync`); `GetBlocksAsync(actorId, query)` reads the actor's `blocks` collection at
   `actorId.BlocksOf()` (through the `CollectionPageCache`, mirroring `GetRepliesAsync`).
   `Iri.BlocksOf()` was added. The three `IActivityPubClient` test stubs gained no-op
   `BlockAsync` / `GetBlocksAsync` to satisfy the widened interface.
 - *Scope note:* this is the **`Block` edge + the `blocks` collection** (the F-07 "Block" half). The
   edge is *recorded but not yet applied* (feed filtering / delivery suppression), and `Mute` / `Flag`
   are still open.
  - `BlockActivityHandlerTests` (10) covers the handler in isolation (local-blocker, local-blocked
    [inverse query], both-remote no-op, idempotent, the no-actor / no-object guards, and the null
    guards). `BlocksCollectionIntegrationTests` (6) covers the live surface: the actor document
    advertises `blocks`, the empty collection is an `OrderedCollection`, a signed inbound `Block`
    (delivered to the target's inbox, signature-validated) records the edge, the `blocks` endpoint
    serves it, a second block appends a second item, and the client's `GetBlocksAsync` reads it back.

## Slice 12.14 — F-07 moderation: apply the block edge (feed exclusion + delivery suppression)

 - `FeedService` (the F-14 followed feed, `GET /ap/v1/u/{handle}/feed`) now reads the actor's `blocks`
   set from `persistence.Moderation.GetBlocksAsync(actorIri)` and **skips any follow the actor has
   blocked** when merging outboxes — a blocked actor's content (local or remote) no longer appears in the
   actor's home timeline. The check is by the follow's actor IRI (the edge is recorded on the actor IRI),
   so it is uniform across local and remote follows.
 - `CreateActivityHandler` (J-18 outbound federation) skips a remote follower who has **blocked the
   author** (`Moderation.IsBlockedAsync(follower, author)`) before scheduling that follower's delivery;
   the post is still surfaced in the author's outbox (J-8). `DeliveryService.DeliverToActorAsync` (the
   generic actor-targeted seam) additionally suppresses a delivery **signed as a local actor** when the
   recipient has blocked the signing actor — a boundary guard; a delivery signed as the instance actor
   (null acting actor) is never suppressed.
 - DI: `AddActivityPubServer` passes `persistence.Moderation` into the `FeedService` and `DeliveryService`
   registrations. Both services take an **optional** `IModerationStore` (null disables the feature →
   pre-F-07 behavior), so a host without a moderation store and the many direct-construction tests are
   unchanged; the default wiring always registers the store, so the feature is on by default.
 - *Scope note:* this is the **application** of the edge Slice 12.13 recorded (no new write path or wire
   surface). The **un-block** (`Undo` of `Block`, via the existing `IModerationStore.RemoveBlockAsync`)
   and `Mute` / `Flag` remain open; F-06 (shared-inbox / relay) is the next item after F-07.
  - `FeedServiceTests` (4) covers feed exclusion in isolation (blocked local follow, blocked remote
    follow, partial block keeps unblocked follows, no-moderation-store includes all).
    `DeliveryQueueAndServiceTests` (4) covers delivery suppression (recipient-blocked-signer suppresses,
    no block delivers, instance-actor skips the check, no-moderation-store never suppresses) — the
    suppressed case is asserted via `queue.Count == 0` (the bounded queue's `TryDequeueAsync` blocks when
    empty). `CreateActivityHandlerTests` (2) covers the J-18 skip (follower who blocked the author is not
    federated to; a follower who did not block is). `BlocksCollectionIntegrationTests` (1) is the
    end-to-end proof: a signed inbound `Block` of a followed actor excludes that actor's post from the
    blocker's followed feed over the wire (present before, absent after).

  ## Result

Wave 1 is effectively closed; the project now has explicit regression coverage for conformance-sensitive semantics, and the major remaining work is in feature completeness and real-world interop testing rather than basic correctness.
