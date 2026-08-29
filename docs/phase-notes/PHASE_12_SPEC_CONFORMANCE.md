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

## Slice 12.15 — F-07 moderation: un-block (`Undo` of `Block`)

 - `UndoActivityHandler` (the F-11 un-follow `Undo` handler) now also handles an `Undo` whose object is a
   `Block`: it resolves the original `Block`'s parties from the local activity store (the `Undo`'s object
   is a reference to the original `Block`, by IRI — the same resolution the follow branch uses for the
   original `Follow`) and removes the `blocker → blocked` edge via `IModerationStore.RemoveBlockAsync` —
   the inverse of `BlockActivityHandler`. The block branch runs before the follow branch (a `Block` has no
   follow target); an `Undo` of any other activity type remains a no-op.
 - Client: `IActivityPubClient` / `ActivityPubClient` gained `UnblockAsync(actorId, targetId, ct)` — the
   inverse of `BlockAsync`. It builds an `Undo` (actor = `actorId`, object = a link to the original
   `Block`'s deterministic `{actor}/blocks/{target}` IRI) and delivers it to `targetId.InboxOf()` (the
   previously-blocked actor's inbox). The `Undo` gets a deterministic unique-per-`(actor, target)` `Id` so
   a retried un-block dedupes.
 - The block and un-block are now **symmetric writes** against the same store (`RecordBlockAsync` /
   `RemoveBlockAsync`); Slice 12.14's feed/delivery logic reads the live edge set, so a block and its later
   un-block automatically flip feed exclusion and delivery suppression on/off. An un-block clears only the
   moderation edge, never the follow edge.
 - *Scope note:* this is the **un-block** (`Undo` of `Block`). `Mute` / `Flag` remain open; F-06
   (shared-inbox / relay) is the next item after F-07.
   - `UndoActivityHandlerTests` (5) covers the un-block in isolation (local-blocker un-block, block-of-local
     un-block [inverse query cleared], block-not-stored no-op, unknown-block-IRI no-op, un-block does not
     touch follow edges); the pre-existing 10 follow/un-follow tests are unchanged (`BuildUndo` generalized
     to `Activity`). `BlocksCollectionIntegrationTests` (1) is the end-to-end proof: `BlockAsync` (202)
     records the edge + the feed excludes the blocked actor's post, then `UnblockAsync` (202) removes the
     edge (the `blocks` collection is empty again) + the feed re-includes the post.

## Slice 12.16 — F-07 moderation: flag (`Flag` + `Undo` of `Flag`)

  - `IModerationStore` gained four **flag** methods, symmetric to the block methods: `RecordFlagAsync`
    (records the directed `flagger → flagged` edge, idempotent), `RemoveFlagAsync` (removes it — the
    un-flag, no-op when absent), `GetFlagsAsync` (the forward flags collection, insertion-ordered), and
    `HasFlaggedAsync` (the directed predicate). `InMemoryModerationStore` implements them against a
    forward-only `_flags` index (reusing the block index's `Add`/`Remove`/`Snapshot`/`Contains` helpers).
    The `flags` collection is served at `GET /ap/v1/u/{handle}/flags` (a paged `OrderedCollection` of
    flagged-actor links) and advertised on the actor document as a `flags` extension link (the same wire
    shape as `blocks`).
  - A new `FlagActivityHandler` records the `flagger → flagged` edge when **either** party is a local
    actor (a local flagger's `flags` collection lists the flagged actor; a local flagged actor is known
    to have been flagged). It mirrors `BlockActivityHandler`'s "either party local" rule and its no-op
    guards (a flag with no resolvable actor/object, a flag between two remote actors). **Unlike a
    `Block`, a `Flag` has no apply half** — it is a moderation *report* a human (or auto-moderator) acts
    on, not an automatic ban, so it does not exclude the flagged actor's content or suppress delivery
    (that is `Block`'s job, Slice 12.14).
  - `UndoActivityHandler` now also handles an `Undo` whose object is a `Flag`: it resolves the original
    `Flag`'s parties from the local activity store (a new `ResolveFlagEdgeAsync` helper mirroring
    `ResolveBlockEdgeAsync`) and removes the `flagger → flagged` edge via `RemoveFlagAsync`. An `Undo` of
    any other activity type (not a `Follow`, a `Block`, or a `Flag`) remains a no-op.
  - Client: `IActivityPubClient` / `ActivityPubClient` gained `FlagAsync` (a signed `Flag` → target inbox,
    deterministic `{actor}/flags/{target}` IRI), `UnflagAsync` (a signed `Undo` of the `Flag` → target
    inbox, the inverse of `FlagAsync`), and `GetFlagsAsync` (enumerates the `flags` collection read
    through the `CollectionPageCache`, the same semantics as `GetBlocksAsync`).
  - *Scope note:* this is the **flag** (`Flag` + `Undo` of `Flag`). `Mute` (no ActivityStreams type —
    Iris-specific, to be handled as a typed Iris extension) remains open; F-06 (shared-inbox / relay) is
    the next item after F-07.
  - `FlagActivityHandlerTests` (10) covers the flag handler in isolation (local flagger, two flags,
    local-of-local, idempotent, local-flagged, both-remote no-op, no-actor/no-object guards, null ctor
    guards). `UndoActivityHandlerTests` (+5) covers the un-flag in isolation (local-flagger un-flag,
    flag-of-local un-flag, flag-not-stored no-op, unknown-flag-IRI no-op, un-flag does not touch block
    edges). `FlagsCollectionIntegrationTests` (6) is the end-to-end proof: the actor document advertises
    the `flags` collection; the empty collection is an `OrderedCollection`; `FlagAsync` (202) records the
    edge + the flag appears in the flagger's `flags`; the client's `GetFlagsAsync` reads back the flagged
    actor's IRI; `UnflagAsync` (202) removes the edge (the `flags` collection is empty again); and a
    `Flag` does **not** exclude the flagged actor's content from the flagger's followed feed (a flag is a
    report, not a block).

## Slice 12.17 — F-07 moderation: mute (`Mute`, local-only) — closes F-07

  - `IModerationStore` gained four **mute** methods, symmetric to the block/flag methods:
    `RecordMuteAsync` (records the directed `muter → muted` edge, idempotent), `RemoveMuteAsync` (removes
    it — the un-mute, returns `true` if an edge was removed), `GetMutesAsync` (the forward mutes
    collection, IRI-sorted), and `IsMutedAsync` (the directed predicate). `InMemoryModerationStore`
    implements them against a forward-only `_mutes` index (reusing the block/flag index's
    `Add`/`Remove`/`Snapshot`/`Contains` helpers). The `mutes` collection is served at
    `GET /ap/v1/u/{handle}/mutes` (a paged `OrderedCollection` of muted-actor links) and advertised on the
    actor document as a `mutes` extension link (the same wire shape as `blocks`/`flags`).
  - A mute is an **Iris-specific** concept with **no ActivityStreams type**, so it is **not** interpreted
    from a federated activity — it is a **local** moderation decision. A new local endpoint
    `POST /ap/v1/u/{handle}/mutes/{**target}` (and `?unmute=true`) authenticates the acting actor via
    `IActorCredentialValidator` (Basic auth) and records/removes the `muter → muted` edge (204 on success,
    401 unauthenticated, 400 unparseable target). It is served by a new `LocalAuthHandler` (adds the
    `Authorization: Basic` header, forwards **unsigned** — not the signed inbox pipeline, which throws for
    an unresolvable signing identity). The route uses a catch-all target (must be the last segment), so
    un-mute is signalled by `?unmute=true` rather than a trailing path segment.
  - The edge is **applied** in `FeedService`: a muted follow's content is **excluded** from the muter's
    home timeline, alongside the existing block exclusion. **Unlike a `Block` (a hard exclusion that
    severs the follow), a mute is a soft exclusion** — the follow is kept, only the content is hidden;
    un-muting restores the content without re-following.
  - Client: `IActivityPubClient` / `ActivityPubClient` gained `MuteAsync` / `UnmuteAsync` (two Basic-auth
    body-less `POST` overloads each, via a shared `LocalModerateAsync` helper) and `GetMutesAsync`
    (enumerates the `mutes` collection read through the `CollectionPageCache`). `ActivityPubClientOptions.
    LocalCredentials` configures the default local credentials; the factory wires a `LocalAuthHandler` from
    them (an explicit per-call credential takes precedence). A request-scoped handler is disposed; a shared
    default is not (an `ownsHandler` rule, so a shared/deferred transport is never disposed).
  - *Scope note (federation):* a probe (`.scratch/muteprobe`) confirmed the library deserializes an unknown
    `type: "Mute"` into a generic `Object` (not an `Activity`), so the inbox endpoint rejects it before any
    handler runs — a federated mute would require a custom JSON converter (a new package-level dependency +
    interop risk). Mute is therefore scoped to **local** state only; federation is explicitly out-of-scope.
    **F-07 moderation is now complete** (Block + Flag + Mute). F-06 (relay / `star`) is the next Phase 12
    item.
  - `MuteStoreTests` (6) covers the mute store in isolation (record → collection + predicate,
    directed/not-mutual, idempotent, sorted-by-IRI, remove, remove-nonexistent returns false).
    `FeedServiceTests` (+4) covers the mute apply in isolation (excludes a local follow, excludes a remote
    follow, partial mute keeps unmuted follows, mute does **not** sever the follow unlike a block).
    `MutesCollectionIntegrationTests` (8) is the end-to-end proof: the actor document advertises the
    `mutes` collection; the empty collection is an `OrderedCollection`; an authenticated `MuteAsync` (204)
    records the edge + the mute appears in the muter's `mutes`; the client's `GetMutesAsync` reads back the
    muted actor's IRI; an unauthenticated request is 401 (no edge); a mute excludes the muted actor's
    content from the feed **without severing the follow** (the follow edge is intact; un-muting restores
    the content); and an un-mute of a nonexistent mute is a no-op (204).

 ## Slice 12.18 — F-06 relay: relay subscription (`star`, local-only)

  - A relay (a `star`-subscribed fan-out server, ActivityPub §5.1.3) is **not** an activity an actor
    receives — it is a **remote fan-out server a local actor points at** to widen reach. So a relay
    subscription is an **Iris-specific local** decision, **not** interpreted from an inbox POST: it is a
    **Basic-authenticated** request to the acting actor's own instance (the same local-decision shape as a
    `Mute`).
  - A new `IRelayStore` / `InMemoryRelayStore` records the directed subscription edge
    `<c>actor → relay</c>` against a forward-only `_relays` index: `RecordRelayAsync` (idempotent),
    `RemoveRelayAsync` (the un-subscribe, returns `true` if an edge was removed), `GetRelaysAsync` (the
    forward relays/`star` collection, IRI-sorted), and `IsRelayAsync` (the directed predicate).
    `InMemoryPersistenceProvider` gained a `Relays` property (and a ctor param, defaulting to a fresh
    store) and `IPersistenceProvider` exposes it.
  - The `relays` collection is served at `GET /ap/v1/u/{handle}/relays` (a paged `OrderedCollection` of
    relay links, the same wire shape as `following`/`mutes` — the collection route regex + `CollectionEndpointHandler`
    gained a `relays` case). It is **advertised on the actor document via the `star` property** (via
    `ExtensionData`, the library's `Person` has no typed `star`; the actor document's `ExtensionData`
    already carried the `mutes`/`blocks`/`flags` links, and `star` is added alongside them, unconditionally
    — every actor may have an empty `relays` set). `IriExtensions.RelaysOf()` builds the collection IRI.
  - A new local endpoint `POST /ap/v1/u/{handle}/relays/{**target}` (and `?unsubscribe=true`) authenticates
    the acting actor via `IActorCredentialValidator` (Basic auth) and records/removes the `actor → relay`
    edge (204 on success, 401 unauthenticated, 400 unparseable target). It is served by a `LocalRelayHandler`
    (mirroring the `LocalMuteHandler`). The route uses a catch-all target (must be the last segment), so
    un-subscribe is signalled by `?unsubscribe=true` rather than a trailing path segment.
  - Client: `IActivityPubClient` / `ActivityPubClient` gained `SubscribeRelayAsync` / `UnsubscribeRelayAsync`
    (two Basic-auth body-less `POST` overloads each) and `GetRelaysAsync` (enumerates the `relays` collection
    read through the `CollectionPageCache`). The private `LocalModerateAsync` was **generalized** into
    `LocalLocalDecisionAsync(actorId, targetId, path, remove, removeQuery, …)` so the same Basic-auth
    local-decision POST serves both mutes (`path="mutes"`, `removeQuery="unmute"`) and relays
    (`path="relays"`, `removeQuery="unsubscribe"`); `LocalModerateAsync` now delegates to it. The existing
    `IActivityPubClient` test stubs gained no-op relay members.
  - *Scope note (fan-out):* this is the **subscription** (configuration) half only. **Relay fan-out** —
    actually delivering a local actor's `Create`/`Announce` content to each subscribed relay (the delivery
    half that gives a relay its reach benefit) — is the **follow-up slice** (12.19), now done (below).
  - `RelayStoreTests` (6) covers the relay store in isolation (record → collection + predicate,
    directed/not-mutual, idempotent, sorted-by-IRI, remove, remove-nonexistent returns false).
    `RelaysCollectionIntegrationTests` (8) is the end-to-end proof: the actor document advertises the
    `star` (relays) collection; the empty collection is an `OrderedCollection`; an authenticated
    `SubscribeRelayAsync` (204) records the edge + the relay appears in the actor's `relays`; the client's
    `GetRelaysAsync` reads back the relay's IRI; an unauthenticated request is 401 (no edge); an
    `UnsubscribeRelayAsync` (`?unsubscribe=true`, 204) removes the edge (the collection is empty again); and
    an un-subscribe of a non-existent subscription is a no-op (204).

 ## Slice 12.19 — F-06 relay: relay fan-out (the delivery half) — closes F-06

   - **Relay fan-out** (the delivery half of F-06) is now wired: when a local actor's `Create` (their own
     post) or `Announce` (their boost) is processed, the handler reads the actor's `relays`/`star` set
     (`IPersistenceProvider.Relays.GetRelaysAsync`) and **adds each subscribed relay to the delivery
     fan-out**, delivering the content to the relay's inbox **signed as the author**. This is the half that
     gives a relay its reach benefit: a relay now actually receives the content of the local actors that
     subscribe to it.
   - `CreateActivityHandler` (the local-person branch) and `AnnounceActivityHandler` each gained a private
     `DeliverToSubscribedRelaysAsync(authorIri, activity, ct)` that, after the existing follower federation,
     calls `IDeliveryService.DeliverToActorAsync(relayIri, activity, authorIri, ct)` for every relay the
     author has subscribed to (reusing the F-01 inbox resolution and F-07 block suppression). A relay is
     always remote (never a local actor), so **no local-actor skip applies** — the relay delivery is
     scheduled unconditionally; a relay that has blocked the author is suppressed by `DeliveryService`
     (F-07) before it is enqueued.
   - `InMemoryDeliveryQueue` gained a `Jobs` property (a point-in-time snapshot of the currently queued
     `DeliveryJob`s, for inspection) so a test can assert which deliveries the handler scheduled (drains and
     re-enqueues, preserving order).
   - `CreateActivityHandlerTests` (4 new unit tests) drives the real `CreateActivityHandler` against a
     recording `IDeliveryService`: a single subscribed relay fans the `Create` out to the relay's inbox
     signed as the author; multiple relays fan out to each; no relays → no fan-out; and a follower **and** a
     relay → both are delivered. `RelayFanOutIntegrationTests` (3 new end-to-end tests, mirroring
     `PostFederationIntegrationTests`) is the wire-level proof: a local author on instance A who has
     subscribed to a relay on instance R posts a `Create` (and, separately, an `Announce`); A's host
     `DeliveryWorker` POSTs the activity to the relay's inbox signed as the author; R validates the
     delivery (resolving the author's key from A's actor document) and stores the activity. A
     no-relay author's post is surfaced locally but **not** fanned out to the relay.
    - *Result:* **F-06 is fully resolved** — a local actor can configure which relays to fan out through
      (12.18) **and** their `Create`/`Announce` content is actually delivered to each subscribed relay
      (12.19).

  ## Slice 12.20 — F-09 `Add` / `Remove` (collection-modification primitives) — closes F-09

    - **`Add`/`Remove` interpretation** (F-09): a new `AddRemoveActivityHandler` interprets the
      ActivityStreams collection-modification primitives. When the **recipient** of the delivery (the inbox
      the activity was posted to — the collection's owner, per `InboxDelivery.RecipientIri`) is a local
      **community** (`Group`), the activity's `object` (the item being added/removed) is **added to /
      removed from the community's member set** via `ICommunityStore.AddMemberAsync` /
      `RemoveMemberAsync`. This is the case for a server that manages a community's membership with the spec's
      `Add`/`Remove` primitives rather than a `Follow`.
    - The handler derives from the **non-generic** `IActivityHandler` (a single
      `ActivityHandlerBase<TActivity>` cannot be parameterized over two activity types) and pattern-matches
      `Add`/`Remove` in `DispatchAsync`, throwing on any other type (the `InboxProcessor` only dispatches an
      `Add` or `Remove` here). `HandledActivityType` is `typeof(Activity)`, so the processor still prefers it
      over the `CommunityInboxActivityHandler` (also registered for `Activity`) as the **most specific**
      handler for both `Add` and `Remove`.
    - **Recipient scoping:** a **person** recipient is a no-op (a person's `followers` are owned by the follow
      lifecycle — `FollowActivityHandler` / `AcceptActivityHandler` — not `Add`/`Remove`), and a **remote**
      community is not this instance's concern. `AddMember` / `RemoveMember` are idempotent, so a re-delivered
      activity (at-least-once delivery, C-07) is safe to re-apply.
    - `AddRemoveActivityHandlerTests` (13 new unit tests) and `AddRemoveFederationIntegrationTests` (4 new
      end-to-end tests, mirroring `MoveFederationIntegrationTests`): a signed `Add` delivered to a local
      community adds the actor as a member (B validates the signature by fetching the sender's key from A's
      actor document, then stores + interprets); a signed `Remove` removes an existing member; a signed `Add`
      to a local **person** is stored but a no-op (no community membership, no follow edge); and an `Add`
      signed by an **unresolvable-key** actor is **rejected (401)** (nothing stored, no member added).
    - *Result:* **F-09 is resolved** — a community's membership is now synchronized from the spec's
      `Add`/`Remove` collection-modification primitives (in addition to the `Follow`-based path).

     ## Result

Wave 1 is effectively closed; the project now has explicit regression coverage for conformance-sensitive semantics, and the major remaining work is in feature completeness and real-world interop testing rather than basic correctness.
