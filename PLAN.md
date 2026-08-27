# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing applications (Blazor clients, ASP.NET Core servers, or any .NET app).

This document is the **index**. The full plan is split across the files below — read them in order for the complete picture.

## Documentation

| File | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns (caching, HTTP signatures, key model, proxy fallback), spec research |
| [docs/PROJECTS.md](docs/PROJECTS.md) | Per-project details: `Iris.Core`, `Iris.Client`, `Iris.Server`, `Iris.Server.InMemory`, `Iris.Client.Extensions` |
| [docs/TESTING.md](docs/TESTING.md) | Integration-first testing strategy, multi-instance `TestServer` harness, test project layout, deferred Mastodon live test |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Phased plan (Phase 0–9+), resolved decisions, open questions |
| [docs/CODING_STYLE.md](docs/CODING_STYLE.md) | **Binding** coding conventions — C# style, naming, error handling, async, and the rules for working with 3rd-party `KristofferStrube.ActivityStreams` types |
| [docs/AUTONOMOUS_LOOP.md](docs/AUTONOMOUS_LOOP.md) | Operating instructions for the autonomous dev loop (one turn at a time) |

## The Short Version

- **Clean and simple** — small, focused abstractions; no framework lock-in beyond .NET itself.
- **One client, two directions** — a single `net10.0` client library used by both client apps (Blazor) and servers (server-to-server).
- **Server as extension** — ActivityPub server capability is added to an existing ASP.NET Core app via `IServiceCollection`/`IApplicationBuilder` extensions.
- **ActivityStreams via `KristofferStrube.ActivityStreams`** — the existing NuGet package provides all ActivityStream/ActivityPub type definitions and JSON-LD serialization. `Iris.Core` does NOT re-implement the object model; it adds Iris-specific concerns (signing, identity, IRI helpers, validation) on top. See [Coding Style — 3rd-Party Types](docs/CODING_STYLE.md#3rd-party-activitystreams-types).
- **Actor-keyed client auth** — the client authenticates to our server (Basic auth in v1), fetches the actor document with the private key, and signs subsequent requests with that key.
- **Layered caching** — client and server cache fetched objects with short TTLs; every cached read has a `bypassCache` / `forceRefresh` escape hatch.
- **Community-aware** — first-class `Group` actors as communities (Lemmy-style) with unified feed/collection APIs.
- **Versioned API surface** — route-prefix versioning (`/ap/v1/...`) from day one; `Iris-Version` meta header; `iris:capabilities` for feature discovery.
- **Integration-first testing** — end-to-end tests against multiple in-process server instances with distinct hostnames, not a sprawl of unit tests.

## Solution Layout

```
Iris.sln
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Server/                net10.0 — ASP.NET Core extensions, endpoints, middleware, community feeds
│   └── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
├── tests/
│   ├── Iris.Testing/               shared multi-instance TestServer harness
│   ├── Iris.Core.Tests/
│   ├── Iris.Client.Tests/
│   └── Iris.Server.Tests/
└── samples/
    ├── SampleServer/               minimal ASP.NET Core app hosting Iris.Server
    └── SampleBlazorClient/         Blazor WebAssembly app using Iris.Client
```

## Conventions (summary)

- **TFM: net10.0** for all projects. C# latest, nullable enabled, file-scoped namespaces.
- **`System.Text.Json` exclusively.** ActivityStream/ActivityPub types come from `KristofferStrube.ActivityStreams` — we do NOT re-implement them.
- **Central package management** (`Directory.Packages.props`).
- **Dependency direction**: `Iris.Core` → `KristofferStrube.ActivityStreams` + BCL; `Iris.Client` → `Iris.Core`; `Iris.Server` → `Iris.Core` + `Iris.Client` + ASP.NET Core; `Iris.Server.InMemory` → `Iris.Server`.
- **Caching**: all cached reads expose a `bypassCache` / `forceRefresh` parameter. No cached path is opaque.
- **Versioning**: route prefix (`/ap/v1/...`) is authoritative; `Iris-Version` header is meta; new capabilities via `iris:`-namespaced terms (configurable namespace base).
- **Testing**: integration-first (xUnit + multi-instance `TestServer` harness); unit tests reserved for pure logic.

> **The full conventions, including the binding rules for 3rd-party ActivityStreams types, are in [docs/CODING_STYLE.md](docs/CODING_STYLE.md).**

## Current Status

- [x] Phase 0 — Scaffolding (see [Roadmap](docs/ROADMAP.md#phase-0--scaffolding-this-step))
  - Scaffold, projects, build, and the multi-instance `TestServer` harness are done and green.
  - Open: (a) spec-research findings not yet captured/folded back (carried into Phase 1 signing work); (b) a bare `dotnet build` in the root is blocked by MSB1011 (stray root scratch files — `rm` is permission-blocked; delete `Program.cs`/`inspect.csproj`/`packages.lock.json` manually).
- [x] Phase 1 — Core: Identity, Keys, Signatures & Caching (complete)
  - Done: `Iri` + `IriExtensions`, `ActivityJson`, the identity/key foundation (`KeyAlgorithm`, `KeyPair`, `KeyPairGenerator`, `IIdentity`/`SystemIdentity`, `IKeyStore`/`InMemoryKeyStore`, `KeyPem`), the **HTTP-signature layer** (`HttpRequestMetadata`, `SigningProfile`, `Signatures`, `SignatureHeader`, `ISignatureSigner`/`HttpSignatureSigner`, `ISignatureVerifier`/`HttpSignatureVerifier`), and the **caching layer** (`CachePolicy`, `CacheState`, `CacheEntry<T>`, `ICache<T>`/`MemoryCache<T>`, `CachedValue<T>`, `CacheExtensions`) — all with unit tests.
   - `dotnet build Iris.slnx` 0 warnings; `dotnet format` clean.
- [x] Phase 2 — Client Library (complete)
  - Done: `IKeyProvider`/`InMemoryKeyProvider` (session actor→key resolution), `SigningHandler` (`DelegatingHandler`; `ClientToServer` for bodyless GETs, `ServerToServer` for body POSTs; host derived from the request URI), `WebFingerClient` + `IDiscoveryService`/`WebFingerDiscoveryService` (RFC 8410 lookup on the account's own host), `ActivityPubClient` + `IActivityPubClient`/`IActivityPubClientFactory` (signing pipeline into an owned `HttpClient`; `GetActorAsync`), `IClientAuthenticator`/`BasicAuthClientAuthenticator` (Basic-auth → owner actor doc + loaded `KeyPair`; algorithm in a `keyAlgorithm` doc extension), **client caching** — `CachingClientCache<TValue>` (async read-through: `bypassCache` + stale-while-revalidate; absent never cached) with `ActorCache`/`CollectionPageCache`/`WebFingerCache`/`KeyCache` (values `IObject`/`IObject`/`Iri`-via-`WebFingerHit`/`JwkKey`; default policies Actor 5m, Page 30s, WebFinger 15m, Key 1h) + `JwkKey`/`WebFingerHit` records, **wired into the call paths** (`ClientCaches` record via `ActivityPubClientOptions.Caches` + factory; `GetObjectAsync`/`GetActorAsync` → `ActorCache`, `GetCollectionAsync` → `CollectionPageCache` honoring `BypassCache`, `WebFingerClient.ResolveActorAsync` → `WebFingerCache`; null cache = no caching), and **rich paged collections** — `CollectionPage` (Iris wrapper over `OrderedCollectionPage`; flattened `Items`, `Iri?` next/prev, `int?` `TotalItems`, `IsLastPage`), `CollectionQuery` (`Limit`/`BypassCache`), `IActivityPubClient.GetCollectionAsync` (follow `first` then `next`, yield pages in order, stop at `Limit`/last page, nothing on 404) + `GetCollectionItemsAsync` (flatten items across pages, limit within a page). Items deserialize from the `items` JSON key (library `Items` maps to `items`, not `orderedItems` — Resolved Decision #22). 69 client unit tests.
  - `dotnet build Iris.slnx` 0 warnings; `dotnet test Iris.slnx` 223/223; `dotnet format` clean.
  - **Handlers:** `JsonLdHandler` (content negotiation: GETs advertise `Accept: activity+json, ld+json`; POSTs get `application/activity+json` when unset) + `RetryHandler` (retries idempotent GETs only on 429/5xx + network failure; honors `Retry-After`; exponential backoff w/ jitter; injectable delay for tests; never replays POSTs). Wired into factory pipeline: `Retry → JsonLd → Signing → transport`. `ActivityPubClientOptions.EnableRetry`/`MaxRetryAttempts`. 12 handler tests. (Resolved Decision #23: retry policy + pipeline order.)
  - **Integration tests (client ↔ live `TestServer`):** `FakeActivityPubServer` (in-process `TestServer`-backed) serves the actor doc, WebFinger, and a two-page outbox collection, recording every request; a flaky mode 503s the first hit per path. 7 integration tests drive the **real** `ActivityPubClient` (real factory, full `Retry → JsonLd → Signing` pipeline) over a genuine HTTP stack: real `Signature`/`Accept`/`Content-Type` headers, WebFinger resolution, multi-page follow, actor + page cache hit/bypass, and retry on a transient 503. (Resolved Decision #24: pre-Phase-3 fake-server topology.)
- [x] Phase 3 — Server Foundation (complete)
  - Done: **persistence interfaces** (`IActorStore`/`IActivityStore`/`IFollowStore`/`IObjectStore`/`ICommunityStore` + the `IPersistenceProvider` aggregate over the five stores + the `IKeyStore`; `IKeyStore` already in `Iris.Core`; the provider is a seam `Iris.Server` never concretizes), **`Iris.Server.InMemory`** (all five in-memory stores + `InMemoryPersistenceProvider` + `AddInMemoryPersistence()` to bind the seam), **`AddActivityPubServer()`/`MapActivityPubEndpoints()`** (versioned `/ap/v1` route group + `Iris-Version` meta header on every response via a group endpoint filter; `ActivityPubServerOptions` config), **actor document endpoint with conditional owner-only `privateKey` (PEM) + `keyAlgorithm` extensions** (Basic auth → `IActorCredentialValidator`; `BasicAuthCredentialValidator` parses the `Authorization: Basic` header and delegates to a host credential-check delegate, constant-time compare; key IRI resolved from the actor's `publicKey.id` or a `#key-1` fallback), **WebFinger** (`acct:{handle}@{host}` → actor IRI, instance host from `BaseUri`), **NodeInfo** (`/nodeinfo/2.0` + `.well-known/nodeinfo` discovery root), **server caching** — `CachingServerCache<TValue>` (async read-through: `forceRefresh` = `?refresh=true` bypass + stale-while-revalidate; absent never cached) + `RemoteActorCache`/`RemoteKeyCache`/`CollectionPageCache`/`WebFingerCache` + `LocalActorDocumentCache` (rendered local actor docs, 60s/300s) + `ServerCaches` aggregate; TTLs wired from `ActivityPubServerOptions.CachePolicies`; **response `Cache-Control`** on the actor doc endpoint (public `max-age=60, stale-while-revalidate=300` via the local cache; owner-only `no-store`; `?refresh=true` → `no-cache`). **27 Server tests**: 12 cache unit tests + 15 live-`TestServer` integration tests (public/auth/wrong-password actor doc, unknown-handle 404, WebFinger + 404, NodeInfo + root, `Iris-Version` header, cache populate/hit/refresh-bypass/no-store-on-auth). `WebHostBuilder` + `TestServer(IWebHostBuilder)` + `UseRouting`/`UseEndpoints` host topology (`IApplicationBuilder` does not implement `IEndpointRouteBuilder` — Resolved Decision #25). (Resolved Decision #26: `CachePolicy.StaleFor` is a *total* usable window from creation, so `StaleFor` must be ≥ `Ttl` for a stale window; the four remote caches use the defaults (no stale window) and Phase 4 can opt into `StaleFor > Ttl` for background revalidation.)
   - `dotnet build Iris.slnx` 0 warnings; `dotnet test Iris.slnx` 249/249 (Core 130, Client 88, Testing 4, Server 27); `dotnet format` clean.
  - [ ] Phase 4 — Inbox & Delivery (in progress — **inbound signature validation + inbox processor pipeline complete**)
    - **Done (inbound signature validation slice):** `ISignatureValidator`+`HttpSignatureValidator` (reconstructs the signature base from the `headers` list in the `Signature` header — accepts **both** signing profiles) + `SignatureValidationMiddleware` (validates POSTs only — GETs skipped to avoid recursive key-resolution bootstrap; outcome in `HttpContext.Items`; unsigned/invalid → 401) + `UseSignatureValidation()`; **inbound key resolution** `IInboundKeyResolver`+`RemoteInboundKeyResolver` (keyId → actor IRI → fetch actor doc over the wire via `IActorDocumentFetcher`+`IrisActorDocumentFetcher` signed as `InstanceActorId` / `NoopActorDocumentFetcher` when none configured → read `publicKey` JWK → `KeyPair.FromJwk` public-only → `RemoteKeyCache`); the validator verifies the resolved key **directly** via a new `ISignatureVerifier.Verify(metadata, key, header)` overload (remote keys are not in the local `IKeyStore` — Resolved Decision #27); **inbox endpoint** `POST /u/{handle}/inbox` (enforce valid signature 401, 404 unknown handle, deserialize `IObjectOrLink`→`Activity` with non-null `Id`, 202); **signing fix** — `SigningHandler` now sends `Digest` + restores `Content-Type` on the signed content (so a receiver can reconstruct the `ServerToServer` base). DI registers the stack + a standalone `RemoteKeyCache`. **Core:** `KeyPair.FromJwk` (public-only, verify-only) + `ExportPublicKeyPem` (PKIX). **Tests:** 5 resolver unit tests + a **two-instance federation integration test** (the first true instance-to-instance federation test) — alice (A) follows bob (B) over live `TestServers`: B fetches A's actor doc over the wire to resolve alice's key, validates the signature, stores the activity (202); unsigned→401; tampered body (digest mismatch)→401.
    - **Done (inbox processor pipeline slice):** `IInboxProcessor` (stores the delivered activity, then dispatches it to the registered handler for its type) + `InboxProcessor` (default: exact `HandledActivityType` match, else closest base-type handler up the hierarchy; unknown types still stored). Handler contract: `IActivityHandler` (non-generic: `HandledActivityType` + `DispatchAsync(delivery, activity, ct)` — threads `CancellationToken`, no reflection) + `IActivityHandler<TActivity>` (strongly-typed `HandleAsync`) + `ActivityHandlerBase<TActivity>` (wires typed `HandleAsync` into the non-generic dispatch). `InboxDelivery` record (`RecipientIri` + `Activity`; recipient = the inbox the activity was delivered to, authoritative for a follow's target). `FollowActivityHandler` records the directed follow edge (follower from `actor`; target = `RecipientIri`) in the `IFollowStore` (the `Accept` response is the delivery slice, not sent here). The inbox endpoint hands the validated activity to the processor (handler failure → 500); DI registers the default handlers + `IInboxProcessor`. **Tests:** 8 `InboxProcessor` unit tests (store+dispatch on Follow, store-only on unmatched activity, exact-beats-base, base-catches, no-actor follow, `Handlers` property, null guards) + a federation integration test proving a signed Follow over the wire records the edge in B's `IFollowStore` (`bob`'s followers now include `alice`).
    - `dotnet build Iris.slnx` 0 warnings; `dotnet test Iris.slnx` 272/272 (Core 135, Client 88, Testing 4, Server 45); `dotnet format` clean.
    - Next: the remaining Phase 4 slices — `IDeliveryService`+`IDeliveryQueue` (in-memory `Channel<T>`), delivery signing with the actor's key (system key for automated events), and the Follow/Accept/Reject flow end-to-end (the outbound `Accept`/`Reject` response delivered back to the follower's inbox, plus `Accept`/`Reject` handlers that finalize/undo the follow state on the follower's side). The four remote object caches built in Phase 3 are the seam the outbound delivery paths will read through.

Track progress in [docs/ROADMAP.md](docs/ROADMAP.md).
