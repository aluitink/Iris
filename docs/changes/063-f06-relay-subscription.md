# 063 — F-06 relay: relay subscription (`star`, local-only)

> 2026-08-29 · Slice 12.18 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Opens gap **F-06** (relay / `star`): a relay (a `star`-subscribed fan-out server, ActivityPub §5.1.3) is
a remote server a local actor points at to widen their content's reach. This slice wires the
**relay-subscription** (configuration) half end to end: a local actor can now subscribe to / un-subscribe
from relays, the subscription set is served as the actor's `relays` (the `star`) collection and advertised
on the actor document, and the client has a one-call round-trip. The **fan-out** (delivery) half —
actually delivering a local actor's content to each subscribed relay — is the follow-up slice (12.19).

A relay subscription is an **Iris-specific local** decision, **not** a federated activity: a relay is not
something an actor *receives*, it is a remote fan-out server an actor *points at*. So — exactly like a
`Mute` (Slice 12.17) — it is **not** interpreted from an inbox POST: it is a **Basic-authenticated**
request to the acting actor's own instance.

- **Relay store.** A new `IRelayStore` / `InMemoryRelayStore` records the directed subscription edge
  `<c>actor → relay</c>` against a forward-only `_relays` index: `RecordRelayAsync(actor, relay, ct)`
  (records the edge, idempotent), `RemoveRelayAsync(actor, relay, ct)` (removes it — the un-subscribe,
  returns `true` if an edge was removed), `GetRelaysAsync(actor, ct)` (the forward relays/`star` collection:
  the relays `actor` subscribes to, IRI-sorted), and `IsRelayAsync(actor, relay, ct)` (the directed
  predicate). `InMemoryPersistenceProvider` gained a `Relays` property (and a ctor param, defaulting to a
  fresh store) and `IPersistenceProvider` exposes it.
- **Local relay endpoint.** A new endpoint `POST /ap/v1/u/{handle}/relays/{**target}` authenticates the
  acting actor via `IActorCredentialValidator` (Basic auth — the same seam the mute endpoint uses) and
  records the `actor → relay` edge; `?unsubscribe=true` on the same route removes it (a catch-all route
  parameter must be the last segment, so un-subscribe is signalled by a query flag rather than a trailing
  path segment). The request is body-less (the target is in the path) and is served by a separate
  `LocalRelayHandler` (not the signed inbox pipeline — `SigningHandler` throws for an unresolvable signing
  identity, and a local Basic-auth request has none). On success it returns 204 (No Content); an
  unauthenticated request is 401; an unparseable target IRI is 400.
- **Relays (`star`) collection + actor-document advertisement.** The `relays` collection is served at
  `/ap/v1/u/{handle}/relays` (a paged `OrderedCollection` of relay links — the same wire shape as
  `following`/`mutes`; the collection route regex + `CollectionEndpointHandler` gained a `relays` case).
  It is **advertised on the actor document via the `star` property** (via `ExtensionData`, the library's
  `Person` has no typed `star`; the actor document's `ExtensionData` already carried the
  `mutes`/`blocks`/`flags` links, and `star` is added alongside them, unconditionally — every actor may
  have an empty `relays` set). `IriExtensions.RelaysOf()` builds the collection IRI, mirroring
  `MutesOf()`.
- **Client.** `IActivityPubClient` / `ActivityPubClient` gained:
  - `SubscribeRelayAsync(actorId, relayId, ct)` / `SubscribeRelayAsync(actorId, relayId, credentials, ct)`
    — a Basic-authenticated body-less `POST` to `{actorId}/relays/{relayId}` (via the `LocalAuthHandler`
    that adds the `Authorization: Basic` header and forwards unsigned). Returns the HTTP status code (204).
  - `UnsubscribeRelayAsync(actorId, relayId, ct)` / `UnsubscribeRelayAsync(actorId, relayId, credentials, ct)`
    — the inverse: `POST` to `{actorId}/relays/{relayId}?unsubscribe=true`.
  - `GetRelaysAsync(actorId, query, ct)` — enumerates the actor's `relays` collection (read through the
    `CollectionPageCache`, the same enumeration/caching semantics as `GetMutesAsync`).
  - The private `LocalModerateAsync` was **generalized** into `LocalLocalDecisionAsync(actorId, targetId,
    path, remove, removeQuery, credentials, ct)` so the same Basic-auth local-decision POST serves both
    mutes (`path="mutes"`, `removeQuery="unmute"`) and relays (`path="relays"`, `removeQuery="unsubscribe"`);
    `LocalModerateAsync` now delegates to it. The existing `IActivityPubClient` test stubs
    (`FeedServiceTests`, `IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests`) gained no-op
    relay members to satisfy the widened interface.

*Scope note (fan-out):* this slice is the **subscription** (configuration) half only. **Relay fan-out** —
actually delivering a local actor's `Create`/`Announce` content to each subscribed relay (the delivery
half that gives a relay its reach benefit) — remains open as the **follow-up slice** (12.19). Until then,
content is still delivered only 1-to-1 to followers.

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/IRelayStore.cs` (NEW) | The relay-subscription store: `RecordRelayAsync`, `RemoveRelayAsync`, `GetRelaysAsync`, `IsRelayAsync` (a directed `actor → relay` edge, forward-only, IRI-sorted). |
| `src/Iris.Server.InMemory/InMemoryRelayStore.cs` (NEW) | `_relays` forward-only index + the four relay method impls (reuses the `Add`/`Remove`/`Snapshot`/`Contains` index helpers). |
| `src/Iris.Server/IPersistenceProvider.cs` | `Relays` property (the `IRelayStore`). |
| `src/Iris.Server.InMemory/InMemoryPersistenceProvider.cs` / `InMemoryPersistenceExtensions.cs` | `Relays` field + ctor param (defaulting to a fresh store); DI registration (`TryAddSingleton<InMemoryRelayStore>` + wiring into the provider factory). |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | Collection route regex widened to include `relays`; a `relays` handler case (`GetRelaysAsync`); the actor document advertises the `star` extension link (→ `/relays`); the new local relay endpoint `POST /u/{handle}/relays/{**target}` (+ `?unsubscribe=true`) with `LocalRelayHandler` (Basic auth via `IActorCredentialValidator`, 204 on success / 401 / 400). |
| `src/Iris.Core/IriExtensions.cs` | `RelaysOf()` (derives `{actor}/relays`), mirroring `MutesOf()`. |
| `src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs` | `SubscribeRelayAsync` (two overloads), `UnsubscribeRelayAsync` (two overloads), `GetRelaysAsync` (enumerates the `relays` collection); the shared `LocalModerateAsync` generalized into `LocalLocalDecisionAsync` (serves both mutes and relays). |
| `tests/Iris.Server.Tests/RelayStoreTests.cs` (NEW) | 6 unit tests (record → collection + predicate, directed/not-mutual, idempotent, sorted-by-IRI, remove, remove-nonexistent returns false). |
| `tests/Iris.Server.Tests/RelaysCollectionIntegrationTests.cs` (NEW) | 8 end-to-end tests (actor doc advertises `star`, empty collection, authenticated subscribe records the edge 204, relay in the `relays` collection, client `GetRelaysAsync` reads back, unauthenticated 401, unsubscribe removes the edge, unsubscribe-nonexistent is a no-op 204). |
| `tests/…` (3 client stubs) | `FeedServiceTests`, `IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests` each gained no-op `SubscribeRelayAsync`/`UnsubscribeRelayAsync`/`GetRelaysAsync` to satisfy the widened interface. |

## Tests

803 → **817** (+14):

- `tests/Iris.Server.Tests/RelayStoreTests.cs` — 6 new. Each drives the real `InMemoryRelayStore`.
  Coverage: a `RecordRelayAsync` makes the relay appear in the actor's `GetRelaysAsync` collection **and**
  satisfy `IsRelayAsync`; the edge is **directed** (the relay's own "relays" collection is empty — a relay
  is not an actor that subscribes to another actor); a **repeated** subscription is idempotent (no
  duplicate); `GetRelaysAsync` is **sorted by IRI**; `RemoveRelayAsync` removes the edge (returns `true`);
  and `RemoveRelayAsync` of a **nonexistent** subscription returns `false` (no-op).
- `tests/Iris.Server.Tests/RelaysCollectionIntegrationTests.cs` — 8 new end-to-end (single instance, bob =
  the local actor who subscribes to a remote relay `relay1.example.com`; a `BasicAuthCredentialValidator`
  for bob). Coverage: the actor document **advertises** the `star` (relays) collection link; the `relays`
  collection is an empty `OrderedCollection` before any subscription; an authenticated
  `SubscribeRelayAsync` (204) **records** the edge and the relay appears in bob's `relays`; bob's own
  `/relays` collection serves the recorded edge as a link; the client's `GetRelaysAsync` **reads back** the
  relay's IRI over the wire; an **unauthenticated** subscribe request is 401 (no edge recorded); an
  `UnsubscribeRelayAsync` (`?unsubscribe=true`, 204) **removes** the edge (the collection is empty again);
  and an un-subscribe of a **nonexistent** subscription is a no-op (204).

## Decisions

- **A relay subscription is local-only — not a federated activity.** Unlike a `Block`/`Flag` (a
  federated `Activity` an actor *receives*), a relay is a remote fan-out server a local actor *points at* —
  it is not something delivered to an inbox. So it is a **Basic-authenticated** request to the acting
  actor's own instance (the same seam as the mute endpoint), not interpreted from the signed inbox
  pipeline (`SigningHandler` throws for an unresolvable signing identity, which a local Basic-auth request
  has none). The endpoint reuses `IActorCredentialValidator`, returns 204 on success, 401 on bad
  credentials, and 400 on an unparseable target IRI.
- **The `relays` collection reuses the `mutes`/`blocks`/`flags` wire shape.** The `relays` endpoint is
  served by the same paged-collection endpoint (the route regex is widened, a new handler case calls
  `GetRelaysAsync`, and the actor document advertises a `star` extension link → `/relays`). The in-memory
  store is a standalone forward-only `_relays` index. This maximizes code reuse and keeps the relay set a
  first-class, wire-visible local collection.
- **The `star` advertisement is unconditional.** Every actor's document carries a `star` extension link to
  its (possibly empty) `relays` collection, so a remote client can always discover whether an actor fans
  out through relays. This mirrors how `mutes`/`blocks`/`flags` are advertised.
- **One route, a query flag for un-subscribe.** ASP.NET requires a catch-all route parameter
  (`{**target}`) to be the last segment (`ASP0017`), so an `/unsubscribe` path segment after it is not
  allowed. The single route `POST /u/{handle}/relays/{**target}` handles both record and remove, signalled
  by `?unsubscribe=true` (the default records a subscription). This keeps the surface to one endpoint.
- **The client's local-decision POST was generalized (mutes + relays).** Both a mute and a relay
  subscription are the same shape (a Basic-authenticated, body-less `POST` to `{actor}/{collection}/{target}`,
  with a query flag for the inverse), so `LocalModerateAsync` was generalized into
  `LocalLocalDecisionAsync(actorId, targetId, path, remove, removeQuery, …)` and `LocalModerateAsync` now
  delegates to it. This avoids duplicating the Basic-auth plumbing and keeps future local decisions (e.g.
  other Iris-specific actor settings) a one-line call.
- **Subscription and fan-out are separate slices.** Subscribing (this slice) and actually delivering to
  the subscribed relays (fan-out, 12.19) are decoupled: the subscription set is a pure local configuration
  store, and the fan-out half will read that store at delivery time (`CreateActivityHandler` /
  `DeliveryService`). This keeps the slice vertically complete and independently testable.
