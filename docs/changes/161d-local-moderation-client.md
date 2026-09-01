# 161d — Split the local-moderation client off the AP protocol layer

Phase 19.0b (AP-native rework), slice 19.0b.3 (client split), step 2 of 2.

## Why

Change 161c (step 1) removed the dead follow-decision client methods, but
`IActivityPubClient` still carried the four **local, non-federated** moderation writes:
`MuteAsync`/`UnmuteAsync` (F-07) and `SubscribeRelayAsync`/`UnsubscribeRelayAsync`
(F-06). A mute and a relay subscription are **not** ActivityStreams activities — there is
no `Mute` or "subscribe-to-relay" type — so they are **not** signed inbox deliveries. They
are Basic-authenticated requests to the acting actor's own home instance, which identifies
the actor from the credentials (`IActorCredentialValidator`) and records or removes the
edge. Keeping them on `IActivityPubClient` contradicted the 19.0b.3 goal: the AP client is
a **pure protocol layer** (outbox-in, inbox-delivery-out). This step moves them to a
dedicated, non-AP surface.

Per the governing principle, these are *specialized local capabilities* (kept, not removed):
they keep their own transport and are later discoverable via the `iris:capabilities`
extension (19.0b.2b relocates the server routes). Only the *client-side* home for the writes
changes here; the *reads* (the actor's `mutes`/`relays` collections) stay on
`IActivityPubClient` because they are ordinary ActivityStreams collection reads.

## What changed

- **New** `src/Iris.Client/ILocalModerationClient.cs` — the local, non-AP moderation surface:
  `MuteAsync`/`UnmuteAsync`/`SubscribeRelayAsync`/`UnsubscribeRelayAsync` (each with a
  no-credential and an explicit-`ProxyCredentials` overload, 8 methods).
- **New** `src/Iris.Client/LocalModerationClient.cs` — the default implementation. A shared
  `LocalAuthHandler` (built from `ActivityPubClientOptions.LocalCredentials` by the factory)
  serves the no-credential overloads; the explicit-credential overloads either wrap that
  shared transport (when a default exists) or build a request-scoped handler over a fresh
  transport (when it does not). A client with neither throws `InvalidOperationException`.
  Each call is a body-less `POST {actorId}/mutes/{targetId}` (or `/relays/{targetId}`), with a
  `?unmute=true` / `?unsubscribe=true` query flag signalling removal, sent unsigned through
  the local-auth handler (not the signing pipeline, which would throw).
- `IActivityPubClient` / `ActivityPubClient`: removed the 4 mute/relay write methods (8 with
  overloads), the `LocalModerateAsync` + `LocalLocalDecisionAsync` helpers, and the now-unused
  `_localAuth` field + all `localAuth` constructor overloads. The interface now exposes only
  AP-protocol methods (the mute/relay *reads* — `GetMutesAsync`/`GetRelaysAsync` — remain).
- `IActivityPubClientFactory` / `ActivityPubClientFactory`: added
  `CreateLocalModerationClient(ActivityPubClientOptions, HttpMessageHandler)` — builds a
  `LocalModerationClient` (with a `LocalAuthHandler` over the transport when
  `LocalCredentials` are set).
- DI/exposure chain: `IrisClientFactory.CreateLocalModerationClient(Iri, HttpMessageHandler?)`
  (credentials from `IrisClientOptions.LocalModeration`/`ProxyCredentials`),
  `IrisClientBundle.CreateLocalModerationClient(...)`, `ClientService.GetLocalModerationClient()`,
  and `ExplorerSession.GetLocalModerationClient()`.
- Sample UI `Pages/ActorDetail.razor`: the Moderation card's mute/unmute and the relay
  card's subscribe/unsubscribe now call
  `Session.GetLocalModerationClient().…` instead of `Session.GetClient().…`.
- Tests:
  - **New** `tests/Iris.Client.Tests/LocalModerationClientTests.cs` (7 unit tests): assert the
    POST path (mutes/relays, with/without the removal query), the Basic-auth header, the
    absence of a body, status-code propagation, the "no credentials" guard, and the
    explicit-credential-with-configured-default wrap.
  - Collection-read integration tests
    (`MutesCollectionIntegrationTests`, `RelaysCollectionIntegrationTests`): added an
    `ILocalModerationClient _local` (built via a new `BuildLocalModerationClient` helper) and
    repointed the write calls to it; the `GetMutesAsync`/`GetRelaysAsync` reads stay on the
    `IActivityPubClient`.
  - Screen tests (`S6MyModerationTests`, `S7ScreenTests`, `S4RelayTests`): the logon helper
    now also returns `session.GetLocalModerationClient()`; mute/unmute/relay writes use it.
  - The 3 `IActivityPubClient` test stubs
    (`IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests`, `FeedServiceTests`):
    the 4 removed write members deleted.
  - The 3 `IActivityPubClientFactory` test stubs
    (`IrisClientFactoryTests.RecordingClientFactory`, `RecreationStabilityIntegrationTests.StubClientFactory`,
    `ObjectPropagationIntegrationTests.StubClientFactory`): implemented the new
    `CreateLocalModerationClient`.

## Decisions

- **`ILocalModerationClient` is not `IDisposable`.** The `LocalModerationClient` holds an
  optional *shared* `LocalAuthHandler` (the factory's transport, which it does not own), and the
  request-scoped handler it may build for an explicit-credential call is disposed per request.
  There is no client-owned resource to dispose, so the interface stays non-disposable (the
  collection-read client, which owns an `HttpClient`, remains disposable).
- **The reads stay on the AP client.** `GetMutesAsync`/`GetRelaysAsync` are ordinary
  ActivityStreams collection reads (an `OrderedCollection` served on the actor document), so they
  belong to the protocol layer; only the *writes* (the non-AP local decisions) move.

## Impact

- Build: clean (`TreatWarningsAsErrors` on).
- Tests: 1,252 passing, 0 failed (+7 new `LocalModerationClient` unit tests over the 1,245
  baseline).

## Remaining (next slice, 19.0b.2b)

Relocate the server routes (`/u/{handle}/mutes/{**target}`, `/u/{handle}/relays/{**target}`, and
the community variants) off the `/ap/v1` AP tree onto a dedicated local-moderation route (e.g.
`/local/v1/...`) so the `/ap/v1` POST surface becomes outbox-only, and add the `mute`/`relay`
`iris:capabilities` values for discovery. The client paths (`{actorId}/mutes/...`,
`{actorId}/relays/...`) will then be updated to the new tree in that step.
