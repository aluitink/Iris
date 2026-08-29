# 062 — F-07 moderation: mute (`Mute`, local-only)

> 2026-08-29 · Slice 12.17 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes the final half of gap **F-07** (moderation): Slices 12.13/12.14/12.15 added the `Block` verb
(record → apply → un-block) and 12.16 added the `Flag` verb (record → un-flag). This slice adds the
**`Mute`** verb — the last remaining moderation operation — end to end. Unlike `Block` and `Flag`, a
`Mute` has **no ActivityStreams type** (it is an Iris-specific concept), so it is **not** interpreted
from a federated activity: it is a **local** moderation decision, made by a local actor against their
own instance.

- **Mute store.** `IModerationStore` gained four mute methods, symmetric to the flag/block methods:
  `RecordMuteAsync(muter, muted, ct)` (records the directed `muter → muted` edge, idempotent),
  `RemoveMuteAsync(muter, muted, ct)` (removes it — the un-mute, returns `true` if an edge was removed),
  `GetMutesAsync(muter, ct)` (the forward mutes collection: the actors `muter` muted, IRI-sorted), and
  `IsMutedAsync(muter, muted, ct)` (the directed predicate). `InMemoryModerationStore` implements them
  against a forward-only `_mutes` index (reusing the same `Add`/`Remove`/`Snapshot`/`Contains` helpers
  as the block/flag indexes). The `mutes` collection is served at `/ap/v1/u/{handle}/mutes` (an
  `OrderedCollection` of muted-actor links) and advertised on the actor document as a `mutes` extension
  link — exactly the wire shape the `blocks`/`flags` collections use.
- **Local mute endpoint.** A mute is a local decision, not a federated activity, so it is **not** an
  inbox delivery. A new endpoint `POST /ap/v1/u/{handle}/mutes/{**target}` authenticates the acting
  actor via `IActorCredentialValidator` (Basic auth — the same seam the actor-doc owner-only extension
  and the proxy endpoint use) and records the `muter → muted` edge; `?unmute=true` on the same route
  removes it (a catch-all route parameter must be the last segment, so un-mute is signalled by a query
  flag rather than a trailing path segment). The request is body-less (the target is in the path) and is
  served by a separate `LocalAuthHandler` (not the signed inbox pipeline — `SigningHandler` throws for
  an unresolvable signing identity, and a local Basic-auth request has none). On success it returns 204
  (No Content); an unauthenticated request is 401; an unparseable target IRI is 400.
- **Apply (feed exclusion).** `FeedService` (the followed feed, `GET /ap/v1/u/{handle}/feed`) now also
  reads the actor's `mutes` set and **excludes a muted follow's content** from that actor's home
  timeline — alongside the existing block exclusion. Unlike a `Block` (a *hard* exclusion that severs
  the follow relationship, Slice 12.14), a mute is a *soft* exclusion: the follow is kept, only the
  muted actor's content is hidden from the feed. Un-muting restores the content without re-following.
- **Client.** `IActivityPubClient` / `ActivityPubClient` gained:
  - `MuteAsync(actorId, targetId, ct)` / `MuteAsync(actorId, targetId, credentials, ct)` — a
    Basic-authenticated body-less `POST` to `{actorId}/mutes/{targetId}` (via a `LocalAuthHandler` that
    adds the `Authorization: Basic` header and forwards unsigned). Returns the HTTP status code (204).
  - `UnmuteAsync(actorId, targetId, ct)` / `UnmuteAsync(actorId, targetId, credentials, ct)` — the
    inverse: `POST` to `{actorId}/mutes/{targetId}?unmute=true`.
  - `GetMutesAsync(actorId, query, ct)` — enumerates the actor's `mutes` collection (read through the
    `CollectionPageCache`, the same enumeration/caching semantics as `GetBlocksAsync`/`GetFlagsAsync`).
  - `ActivityPubClientOptions.LocalCredentials` (a `ProxyCredentials?`) configures the default local
    credentials; the factory wires a `LocalAuthHandler` from them. An explicit per-call credential
    (the `MuteAsync`/`UnmuteAsync` overloads) takes precedence over the default.

The mute and un-mute are now symmetric writes against the same `IModerationStore`: `RecordMuteAsync` and
`RemoveMuteAsync`, mirroring the block's `RecordBlockAsync`/`RemoveBlockAsync` and the flag's
`RecordFlagAsync`/`RemoveFlagAsync`. **F-07 moderation is now complete** (Block + Flag + Mute).

*Scope note (federation):* a `Mute` cannot be interpreted over the wire. A probe confirmed the
ActivityStreams library deserializes an unknown `type: "Mute"` into a generic `Object` (not an
`Activity`), so the inbox endpoint rejects it before any handler runs. Interpreting a federated mute
would require registering a custom JSON converter (a new package-level dependency + interop risk). Mute
is therefore scoped to **local** state only (the instance's own actors); federation is explicitly
out-of-scope and recorded here.

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/IModerationStore.cs` | Four new mute methods (`RecordMuteAsync`, `RemoveMuteAsync`, `GetMutesAsync`, `IsMutedAsync`), symmetric to the flag/block methods. |
| `src/Iris.Server.InMemory/InMemoryModerationStore.cs` | `_mutes` forward-only index + the four mute method impls (reuses the block/flag index's `Add`/`Remove`/`Snapshot`/`Contains` helpers). |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | Collection route regex widened to include `mutes`; a `mutes` handler case (`GetMutesAsync`); the actor document advertises the `mutes` extension link; the new local mute endpoint `POST /u/{handle}/mutes/{**target}` (+ `?unmute=true`) with `LocalMuteHandler` (Basic auth via `IActorCredentialValidator`, 204 on success / 401 / 400). |
| `src/Iris.Core/IriExtensions.cs` | `MutesOf()` (derives `{actor}/mutes`), mirroring `BlocksOf()`/`FlagsOf()`. |
| `src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs` | `MuteAsync` (two overloads), `UnmuteAsync` (two overloads), `GetMutesAsync` (enumerates the `mutes` collection); a shared `LocalModerateAsync` helper (Basic-auth body-less `POST`); a `_localAuth` field + ctor overloads; an `ownsHandler` disposal rule (a request-scoped handler is disposed; a shared default is not). |
| `src/Iris.Client/LocalAuthHandler.cs` (NEW) | A `DelegatingHandler` that adds the `Authorization: Basic` header and forwards the request **unsigned** (does not go through the `SigningHandler`, which throws for a local request). |
| `src/Iris.Client/ActivityPubClientOptions.cs` | `LocalCredentials` option (a `ProxyCredentials?`). |
| `src/Iris.Client/ActivityPubClientFactory.cs` | Wires a `LocalAuthHandler` from `options.LocalCredentials` when set. |
| `src/Iris.Server/FeedService.cs` | `GetFeedAsync` now fetches the `mutes` set and excludes a muted follow (`blocked.Contains(followIri) \|\| muted.Contains(followIri)`) — a soft exclusion (the follow is kept, unlike a block). |
| `tests/Iris.Server.Tests/MuteStoreTests.cs` (NEW) | 6 unit tests (record → collection + predicate, directed/not-mutual, idempotent, sorted-by-IRI, remove, remove-nonexistent returns false). |
| `tests/Iris.Server.Tests/FeedServiceTests.cs` | 4 new unit tests (mute excludes a local follow, excludes a remote follow, partial mute keeps unmuted follows, mute does **not** sever the follow unlike a block). |
| `tests/Iris.Server.Tests/MutesCollectionIntegrationTests.cs` (NEW) | 8 end-to-end tests (actor doc advertises `mutes`, empty collection, authenticated mute records the edge 204, mute in the `mutes` collection, client `GetMutesAsync` reads back, unauthenticated 401, mute excludes feed content **without** severing the follow + un-mute restores, un-mute-nonexistent is a no-op 204). |
| `tests/…` (3 client stubs) | `FeedServiceTests`, `IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests` each gained no-op `MuteAsync`/`UnmuteAsync`/`GetMutesAsync` to satisfy the widened interface. |

## Tests

785 → **803** (+18):

- `tests/Iris.Server.Tests/MuteStoreTests.cs` — 6 new. Each drives the real
  `InMemoryModerationStore`. Coverage: a `RecordMuteAsync` makes the muted actor appear in the muter's
  `GetMutesAsync` collection **and** satisfy `IsMutedAsync`; the edge is **directed** (the muted actor's
  own mutes collection is empty — a mute is not mutual); a **repeated** mute is idempotent (no duplicate);
  `GetMutesAsync` is **sorted by IRI**; `RemoveMuteAsync` removes the edge (returns `true`); and
  `RemoveMuteAsync` of a **nonexistent** mute returns `false` (no-op).
- `tests/Iris.Server.Tests/FeedServiceTests.cs` — 4 new. Each drives the real `FeedService` against an
  `InMemoryPersistenceProvider`. Coverage: a **muted local follow** is excluded from the feed; a **muted
  remote follow** is excluded; a **partial mute** (two follows, one muted) keeps the unmuted follow's
  content; and a mute **does not sever the follow** (unlike a block — the follow edge is still present,
  only the content is hidden).
- `tests/Iris.Server.Tests/MutesCollectionIntegrationTests.cs` — 8 new end-to-end (single instance, bob =
  muter, carol = muted local; a `BasicAuthCredentialValidator` for bob). Coverage: the actor document
  **advertises** the `mutes` collection link; the `mutes` collection is an empty `OrderedCollection`
  before any mute; an authenticated `MuteAsync` (204) **records** the edge and carol appears in bob's
  `mutes`; bob's own `/mutes` collection serves the recorded edge as a link; the client's
  `GetMutesAsync` **reads back** the muted actor's IRI over the wire; an **unauthenticated** mute request
  is 401 (no edge recorded); a mute **excludes** the muted actor's content from the muter's followed feed
  **without severing the follow** (the follow edge is intact; un-muting restores the content); and an
  un-mute of a **nonexistent** mute is a no-op (204).

## Decisions

- **A mute is local-only — no federation.** The decisive design difference from `Block`/`Flag` is that a
  `Mute` has **no ActivityStreams type**, so it cannot be a federated activity. A probe (`.scratch/muteprobe`)
  confirmed the library deserializes an unknown `type: "Mute"` into a generic `Object` (not an `Activity`),
  so the inbox endpoint rejects it before any handler runs. Interpreting a federated mute would require a
  custom JSON converter (a new package-level dependency + interop risk), so mute is scoped to **local**
  state only. A mute is a Basic-authenticated request to the acting actor's own instance — the same seam
  as the actor-doc owner-only extension and the proxy endpoint.
- **A mute is a soft exclusion, a block is a hard exclusion.** The apply half in `FeedService` hides the
  muted actor's content from the muter's feed, but **keeps the follow** (the follow edge is untouched).
  This is the inverse of a `Block` (Slice 12.14), which severs the relationship (the follow is removed and
  delivery is suppressed). The integration test `Mute_ExcludesContentFromFeed_WithoutSeveringFollow` pins
  this boundary (the follow edge is intact after a mute; un-muting restores the content without
  re-following).
- **The mute endpoint is Basic-authenticated, not a signed inbox delivery.** A local Basic-auth request
  has no signing identity, and `SigningHandler` throws for an unresolvable identity — so the request is
  served by a separate `LocalAuthHandler` (adds `Authorization: Basic`, forwards unsigned) rather than the
  signed pipeline. The endpoint reuses `IActorCredentialValidator` (the existing credential seam), returns
  204 on success, 401 on bad credentials, and 400 on an unparseable target IRI.
- **One route, a query flag for un-mute.** ASP.NET requires a catch-all route parameter (`{**target}`) to
  be the last segment (`ASP0017`), so an `/unmute` path segment after it is not allowed. The single route
  `POST /u/{handle}/mutes/{**target}` handles both record and remove, signalled by `?unmute=true` (the
  default records a mute). This keeps the surface to one endpoint.
- **The `mutes` collection reuses the `blocks`/`flags` wire shape and store helpers.** The `mutes`
  endpoint is served by the same paged-collection endpoint (the route regex is widened, a new handler
  case calls `GetMutesAsync`, and the actor document advertises a `mutes` extension link). The in-memory
  store reuses the block/flag index's `Add`/`Remove`/`Snapshot`/`Contains` helpers for a third `_mutes`
  dictionary. This maximizes code reuse and keeps the mute a first-class, wire-visible local moderation
  collection.
- **The client's local-mute request is body-less and unsigned.** The target is in the path (a catch-all
  preserving the absolute IRI), so the request has no body and is sent through the `LocalAuthHandler`
  (not the signed pipeline). A shared default `LocalAuthHandler` (from `ActivityPubClientOptions.
  LocalCredentials`) is reused across calls and **not** disposed by a request-scoped `HttpClient`; a
  request-scoped handler (explicit credentials over a fresh transport) **is** disposed — an `ownsHandler`
  rule that prevents disposing a shared (possibly deferred, in tests) transport.
