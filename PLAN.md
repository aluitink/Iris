# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing applications (Blazor clients, ASP.NET Core servers, or any .NET app).

## Design Principles

- **Clean and simple** — small, focused abstractions; no framework lock-in beyond .NET itself.
- **One client, two directions** — a single `net10.0` client library used both by client apps (browser/Blazor) and by servers (server-to-server).
- **Server as extension** — ActivityPub server capability is added to an existing ASP.NET Core app via `IServiceCollection`/`IApplicationBuilder` extensions.
- **Abstracted persistence** — the server stores data behind interfaces; the initial implementation is in-memory.
- **Proxied request fallback** — when a client hits CORS or 401/403 talking to a remote server, the request can be cascaded through *our* server (via `DelegatingHandler`) which signs and forwards it.
- **ActivityStreams via `KristofferStrube.ActivityStreams`** — we use the existing NuGet package for all ActivityStream/ActivityPub type definitions and JSON-LD serialization. `Iris.Core` does NOT re-implement the object model; it adds Iris-specific concerns (signing, identity, IRI helpers, validation) on top.
- **Actor-keyed client auth** — the client authenticates to *our* server (Basic auth in v1), fetches the actor document which includes the private key, and signs subsequent client→server requests with that key. Server→server requests are signed by the actor's key (or system key for automated events).
- **Layered caching** — both client and server cache fetched objects (actors, collections, pages) with short TTLs to reduce chatter. Every cached read has a `bypassCache` / `forceRefresh` escape hatch.
- **Community-aware** — first-class support for `Group` actors as communities (Lemmy-style). Communities can follow other communities/actors, receive their content in a community inbox, and propagate it to local members. The client exposes unified feed/collection APIs that work identically for personal actors and communities.
- **Versioned API surface** — public API is versioned from day one via a **route prefix** (e.g. `/ap/v1/...`), an early first-class design decision. Library packages carry semantic versions; a version header (e.g. `Iris-Version`) is emitted as meta information. New major versions add a new prefix; existing prefixes stay stable and additive. A `?refresh=true`-style escape hatch keeps old clients working.
- **Configurable namespace** — the `iris:` namespace IRI is configurable per-deployment so forks can extend it with additional terms/capabilities. Collections advertise their extended features via an `iris:capabilities` property.
- **Integration-first testing** — tests are end-to-end against real in-process server instances (multiple hostnames), not a sprawl of small unit tests. Multiple in-memory server instances federate with each other to prove instance-to-instance compatibility. A downstream goal is a live Mastodon compatibility test orchestrated via Mastodon's admin API.

## Solution Layout

```
Iris.sln
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions (on top of ActivityStreams)
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Server/                net10.0 — ASP.NET Core extensions, endpoints, middleware, community feeds
│   └── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
├── tests/
│   ├── Iris.Core.Tests/
│   ├── Iris.Client.Tests/
│   └── Iris.Server.Tests/
└── samples/
    ├── SampleServer/               minimal ASP.NET Core app hosting Iris.Server
    └── SampleBlazorClient/         Blazor WebAssembly app using Iris.Client
```

## Project Details

### 1. `Iris.Core` (net10.0)

Shared primitives built **on top of** `KristofferStrube.ActivityStreams`. No HTTP, no DI, no persistence.

> **Dependency**: `KristofferStrube.ActivityStreams` (v0.2.4, net7.0 — fully compatible with net10.0). Provides: `Object`, `Actor`, `Person`, `Application`, `Service`, `Group`, `Organization`, `Activity`, `IntransitiveActivity`, all activity subtypes (`Follow`, `Accept`, `Reject`, `Create`, `Like`, `Announce`, `Undo`, etc.), all object subtypes (`Note`, `Article`, `Image`, `Video`, etc.), `Collection`, `OrderedCollection`, `OrderedCollectionPage`, `Link`, `ILink`, `IObjectOrLink`, `Endpoints` (with `ProxyUrl`, `SharedInbox`, `ProvideClientKey`, `SignClientKey`), built-in `System.Text.Json` converters, and `@context` term definitions.

- **IRI helpers**: `Iri` value type (wraps `Uri`), relative/absolute resolution, `#Public` constant, `IriExtensions` for common ActivityPub patterns (inbox/outbox derivation from actor IRI).
- **Identity & Keys**:
  - `IIdentity` — `Iri Id`, `Iri KeyId`, `string? Name`, `string? PreferredUsername`.
  - `SystemIdentity : IIdentity` — the server's `Application`-type actor.
  - `KeyPair` — wraps `RSA`/`ECDsa` private key + public key; `KeyId` IRI; `Algorithm` (rsa-sha256 / ecdsa-sha256).
  - `KeyPairGenerator` — static helpers to generate RSA-2048 or EC P-256 key pairs.
  - `IKeyStore` — interface for persisting/retrieving key pairs by `KeyId` (implemented by server persistence).
- **HTTP Signatures** (pure crypto, no HTTP):
  - `ISignatureSigner` — `string Sign(HttpRequestMetadata request, KeyPair key, string[] headers)`.
  - `ISignatureVerifier` — `bool Verify(HttpRequestMetadata request, string signatureHeader, PublicKey publicKey)`.
  - `HttpRequestMetadata` — value type: `Method`, `Uri`, `Headers` (ordered dict), `BodyHash` (optional).
  - `RsaSignatureSigner` / `RsaSignatureVerifier`, `EcdsaSignatureSigner` / `EcdsaSignatureVerifier`.
  - Signature base construction per [draft-cavage-http-signatures-03](https://datatracker.ietf.org/doc/html/draft-cavage-http-signatures-03) as used by ActivityPub.
  - **Client→Server header constraint**: in Blazor/WASM, `fetch`/XHR cannot set arbitrary headers (CORS `Access-Control-Allow-Headers` limits). The client→server signing profile uses a **restricted header set**: `(request-target) host date` only. The server's `ISignatureVerifier` must accept this reduced set for client-originated requests. Server→server uses the full set: `(request-target) host date digest content-type`.
- **Validation**: `IActivityValidator` + default structural validator (required fields, type checks) — operates on `KristofferStrube.ActivityStreams` types.
- **Events** (in-process, used by server): `ActivityReceived`, `ActivityDelivered`, `FollowRequested`, etc.
- **JSON helpers**: `ActivityJson` static class — pre-configured `JsonSerializerOptions` with the ActivityStreams converters registered, `@context` injection, and content-type constants (`application/activity+json`).
- **Caching abstractions** (interfaces + in-memory default; no HTTP):
  - `ICache<T>` — `Task<T?> GetAsync(string key, CancellationToken ct)`, `Task SetAsync(string key, T value, TimeSpan ttl, CancellationToken ct)`, `Task InvalidateAsync(string key, CancellationToken ct)`, `Task ClearAsync(CancellationToken ct)`.
  - `CacheEntry<T>` — `T Value`, `DateTimeOffset ExpiresAt`, `bool IsExpired`.
  - `MemoryCache<T>` — `ConcurrentDictionary`-backed, TTL-based expiry, optional max-entry count with LRU eviction.
  - `CachePolicy` — `TimeSpan DefaultTtl`, `TimeSpan StaleTtl` (serve stale while revalidating), `bool EnableStaleWhileRevalidate`.
  - **Design**: the cache is a value-type-agnostic abstraction. `Iris.Client` and `Iris.Server` each compose their own typed caches (e.g. `ActorCache`, `CollectionPageCache`) on top of `ICache<T>`.

### 2. `Iris.Client` (net10.0)

The single client used by Blazor apps *and* by servers for server-to-server calls.

- **`IActivityPubClient`** — high-level API (operates on `KristofferStrube.ActivityStreams` types):
  - `GetActorAsync(Iri id, bool bypassCache = false)` → `Actor`
  - `GetInboxAsync(Iri actorId, int? page = null, bool bypassCache = false)` / `GetOutboxAsync(...)` → `OrderedCollectionPage`
  - `SendActivityAsync(Iri targetInbox, Activity activity, ...)` — POST to inbox
  - `FollowAsync(Iri targetActor)`, `UnfollowAsync(...)`
  - `CreateAsync(Iri targetInbox, Object obj)` — wraps in `Create`
  - `LikeAsync(Iri targetObject)`, `AnnounceAsync(...)`, `UndoAsync(...)`
  - `FetchCollectionPageAsync(Iri collection, Iri? next, bool bypassCache = false)`
  - `GetCollectionAsync(Iri collection)` → `IAsyncEnumerable<CollectionPage>` — **rich paged enumeration** (see below)
  - `GetCommunityFeedAsync(Iri communityId, FeedFilter? filter = null)` → `IAsyncEnumerable<Object>` — unified community/personal feed
- **`IActivityPubClientFactory`** — creates clients bound to a specific identity (key pair + actor IRI).
- **Rich Paged Collection Enumeration**:
  - `IAsyncEnumerable<CollectionPage>` — the client exposes collections as async-enumerable sequences of pages. Each `CollectionPage` wraps an `OrderedCollectionPage` with:
    - `IReadOnlyList<IObjectOrLink> Items` — the page's objects.
    - `Iri? NextPage` — the `next` link (null when exhausted).
    - `Iri? PrevPage` — the `prev` link.
    - `int? TotalItems` — from `totalItems` if the server provides it.
    - `bool IsLastPage` — convenience: `NextPage == null`.
  - **Usage pattern**:
    ```csharp
    await foreach (var page in client.GetCollectionAsync(actor.Outbox.Href!))
    {
        foreach (var item in page.Items) { /* process */ }
        if (page.IsLastPage) break;
    }
    ```
  - **Flattened variant**: `IAsyncEnumerable<IObjectOrLink> GetCollectionItemsAsync(Iri collection)` — transparently follows `next` links and yields individual objects across all pages. Caller can `break` early to stop fetching.
  - **Filtering**: `CollectionQuery` options — `int? PageSize`, `Func<IObjectOrLink, bool>? Predicate` (client-side filter), `TimeSpan? Since` (skip items older than), `bool BypassCache`.
  - **Caching**: each page is cached by its IRI (the `next` link value) with a short TTL (default 30s). The first page (the collection IRI itself) is cached slightly longer (60s). `bypassCache: true` skips the cache and refreshes the entry.
- **Client-side Caching**:
  - `IClientCache` — composed from `ICache<T>` (in `Iris.Core`):
    - `ActorCache` — `Iri → Actor`, TTL 5 min (actors rarely change). `bypassCache` forces re-fetch.
    - `CollectionPageCache` — `Iri (page URL) → OrderedCollectionPage`, TTL 30s.
    - `WebFingerCache` — `resource string → WebFingerResponse`, TTL 1 hr.
    - `KeyCache` — `Iri (keyId) → PublicKey`, TTL 1 hr (for verifying remote signatures).
  - **Stale-while-revalidate**: if an entry is past `DefaultTtl` but within `StaleTtl`, the cache returns the stale value immediately and triggers a background refresh. The caller gets fast responses; the next call gets fresh data.
  - **Invalidation**: `InvalidateActorAsync(Iri)`, `InvalidateCollectionAsync(Iri)` — called after local mutations (e.g. after posting to outbox, invalidate the outbox's first page).
  - **Blazor-friendly**: cache is in-memory per `IActivityPubClient` instance. No `System.Runtime.Caching` dependency (not available in WASM).
- **Client Authentication & Key Acquisition** (the Iris-specific flow):
  - `IClientAuthenticator` — interface for authenticating to *our* server to obtain an actor's signing key.
  - `BasicAuthClientAuthenticator` (v1) — sends `Authorization: Basic base64(user:pass)` to `GET /{actorHandle}` on our server. The server, upon successful auth, returns the actor document **with an additional `privateKey` field** (JWK or PEM) alongside the normal `publicKey`. The client extracts and holds the private key in memory.
  - `IKeyProvider` — in-memory store of the acquired `KeyPair` for the session. Not persisted to disk (security: key lives only in process memory for the lifetime of the client session).
  - **Flow**:
    1. Client calls `IActivityPubClientFactory.CreateForActorAsync(handle, credentials)`.
    2. Factory calls `IClientAuthenticator.AuthenticateAsync(handle, credentials)` → our server returns actor doc + private key.
    3. Factory constructs `KeyPair` from the private key, stores in `IKeyProvider`.
    4. Returns an `IActivityPubClient` bound to that identity. All subsequent requests are signed with the actor's key.
  - **Header constraint (Blazor/WASM)**: client→server signatures use only `(request-target) host date` — headers that `fetch`/XHR are permitted to set. The `SigningHandler` has a `SigningProfile` enum: `ClientToServer` (restricted) vs `ServerToServer` (full header set including `digest`, `content-type`).
- **HTTP pipeline** (all `DelegatingHandler`s, composable):
  1. `SigningHandler` — adds `Signature` header using the identity's `KeyPair`. Respects `SigningProfile` (restricted vs full headers).
  2. `ProxyFallbackHandler` — on `401`/`403`/CORS-failure (in Blazor: network error / `OperationCanceledException` heuristics), re-issues the request to our own server's proxy endpoint (`/ap/proxy/{base64url(target)}`) with the client's Basic auth credentials; server signs with the actor's key (or system key) and forwards. Configurable: enabled/disabled, which status codes trigger, max retry count.
  3. `RetryHandler` — exponential backoff for 429/5xx.
  4. `JsonLdHandler` — attaches `Content-Type: application/activity+json`, `Accept`, and `@context` injection.
- **Discovery**:
  - `IDiscoveryService` — fetch actor document, follow `rel="lrdd"` / `rel="http://webfinger.net/rel/profile"` links, resolve inbox/outbox.
  - `WebFinger` client — `application/jrd+json` parse.
- **Blazor-friendly**: all APIs are `async Task<T>`, no `HttpClient` ownership assumptions (accepts `IHttpClientFactory` or a pre-built `HttpClient`), works in WASM (no `System.Net.Http.SocketsHttpHandler` dependencies).

### 3. `Iris.Server` (net10.0)

ASP.NET Core extension package. Adds ActivityPub server capability to any existing app.

- **Service extensions**:
  ```csharp
  services.AddActivityPubServer(options => {
      options.ServerIri = new Iri("https://example.com");
      options.SystemIdentity = ...;          // IIdentity for the server
      options.RoutePrefix = "/ap/v1";        // versioned route prefix (default "/ap/v1")
      options.NamespaceIri = new Iri("https://iris.example/ns#"); // configurable; forks override
      options.UseInMemoryPersistence();      // or .UseXxxPersistence()
  });
  ```
- **Endpoint extensions** (`IEndpointRouteBuilder`):
  - `MapActivityPubEndpoints()` — registers (all under the **versioned route prefix**, e.g. `/ap/v1/...`; the prefix is an early design decision, see Resolved #10):
    - `GET /{actorHandle}` — actor document (JSON-LD, `application/activity+json`). **When the request is authenticated** (Basic auth in v1, matched to the actor's owner), the response includes an additional `privateKey` property (PEM) alongside the standard `publicKey` (JWK). Unauthenticated requests get the public document only.
    - `POST /{actorHandle}/inbox` — receive activities (signature-validated)
    - `GET /{actorHandle}/outbox` — paginated outbox
    - `GET /{actorHandle}/followers` / `/following` — collections
    - `GET /.well-known/webfinger` — WebFinger (unversioned, per RFC)
    - `GET /.well-known/nodeinfo` + `GET /nodeinfo/2.0` — NodeInfo (unversioned, per spec)
    - `POST /ap/proxy/{target}` — **proxied request endpoint** (see below)
    - `GET /{actorHandle}/.well-known/...` — key document (optional)
  - **Versioning**: the route prefix (e.g. `/ap/v1`) is the authoritative version. A version header (e.g. `Iris-Version: 1`) is emitted on responses as meta information. New major versions add a new prefix; existing prefixes stay stable.
- **Client Authentication** (v1: Basic):
  - `IActorCredentialValidator` — interface: `Task<Actor?> ValidateAsync(string handle, string username, string password)`. The host app implements this to check credentials against its own user store.
  - `BasicAuthActorCredentialValidator` — default implementation that calls `IActorCredentialValidator`; on success, the actor document endpoint includes the private key.
  - The private key is served as a JWK (`{"kty":"RSA","n":"...","e":"...","d":"...",...}`) in a `privateKey` property on the actor document. This is **only** included when auth succeeds.
  - **Security note**: the private key transit is protected by TLS + Basic auth. In a later phase, this can be upgraded to OAuth2 bearer tokens or a key-exchange protocol. The `IActorCredentialValidator` abstraction makes the swap transparent.
- **HTTP Signature validation**:
  - `ISignatureValidator` — parses `Signature` header, reconstructs signature base, verifies against the sender's public key (fetched/cached from their actor document).
  - `SignatureValidationMiddleware` — applied to inbox endpoints; rejects invalid signatures with `401`.
  - Key cache: `IKeyCache` (in-memory by default, TTL-based).
- **Persistence abstractions** (interfaces in `Iris.Server`, implementations in separate packages):
  - `IActorStore` — Get/Save/Delete actors, lookup by handle/IRI.
  - `IActivityStore` — Append to inbox/outbox, query with pagination, filter by type.
  - `IFollowStore` — Follow/Follower relationships, accept/reject state.
  - `IObjectStore` — Store/fetch ActivityStream objects by IRI.
  - `IKeyStore` — Server key pairs (system identity + per-actor keys).
  - `IPersistenceProvider` — factory that returns all of the above in a unit of work.
- **Delivery**:
  - `IDeliveryService` — takes an outgoing activity, resolves recipient inboxes, signs with the sender's key, POSTs via `IActivityPubClient` (reusing the client library).
  - `IDeliveryQueue` — abstraction for queuing (in-memory `Channel<T>` initially; swap for DB/queue later).
  - Retry + backoff on failure; dead-letter log.
- **Inbox processing**:
  - `IInboxProcessor` — pipeline: validate signature → validate structure → deduplicate → dispatch to handlers.
  - `IActivityHandler<TActivity>` — register handlers per activity type (e.g. `FollowHandler`, `CreateHandler`).
  - Handlers emit domain events; the host app subscribes to do business logic.
- **Proxied request endpoint** (`/proxy/{target}`):
  - Accepts `POST` with the original request's method/path/headers/body (base64url-encoded target IRI in route).
  - Authenticates the caller (client identity via signature or bearer).
  - Signs the request with the system identity (or the caller's identity if they uploaded a key), forwards to the target, streams the response back.
  - Rate-limited; only allows `GET`/`POST` to `application/activity+json` endpoints initially.
- **WebFinger / NodeInfo**: generated from `IActorStore` + server options.
- **Actor management API** (optional, for the host app):
  - `IActorManager` — create local actors, generate keys, assign handles.
- **Community / Group Support** (Lemmy-style):
  - A **community** is a `Group` actor (from `KristofferStrube.ActivityStreams`) with a local handle (e.g. `/c/{communityName}`). It has its own inbox, outbox, followers, and following — identical in shape to a `Person` actor.
  - **`ICommunityStore`** — extends `IActorStore` with community-specific queries:
    - `GetCommunityAsync(string name)` → `Group`
    - `GetMemberAsync(Iri communityId, Iri actorId)` → membership record
    - `GetMembersAsync(Iri communityId, int page)` → paginated member list
    - `GetCommunitiesForActorAsync(Iri actorId)` → communities the actor belongs to
    - `GetFollowedCommunitiesAsync(Iri actorId)` → communities the actor follows
  - **Community following**: a community (Group actor) can `Follow` another community or actor. When the followed community posts (a `Create` activity), it is delivered to the following community's inbox. The following community can then **propagate** the content to its local members (via their inboxes or a community feed).
  - **`ICommunityFeedService`** — builds a unified feed for a community:
    - `GetFeedAsync(Iri communityId, FeedFilter? filter)` → `IAsyncEnumerable<Object>`
    - Sources: (1) local members' posts addressed to the community, (2) content from followed communities/actors (received via inbox and stored), (3) optionally cross-posted/announced content.
    - `FeedFilter` — `FeedSort` (New / Top / Active), `TimeWindow` (Day / Week / Month / All), `string? Tag`, `bool IncludeFollowed`.
  - **Community inbox processing**: when a `Create` activity arrives at a community's inbox from a followed remote community, the `CommunityInboxHandler` stores the object in `IObjectStore` tagged with the community, making it available in the community feed.
  - **Endpoints**:
    - `GET /c/{communityName}` — community document (Group actor). Includes an **`iris:capabilities`** property (in the configurable namespace) declaring available sub-collections/features (e.g. `feed`, `members`, `search`).
    - `GET /c/{communityName}/members` — paginated member list.
    - `GET /c/{communityName}/feed` — community feed (ActivityStreams `OrderedCollectionPage`, shared `limit`/`offset` shape).
    - `GET /c/{communityName}/search` — specialized search collection (extended query params; capability-gated via `iris:capabilities`).
    - `POST /c/{communityName}/inbox` — community inbox (same signature validation as actor inbox).
  - **Unified client experience**: the client's `GetCommunityFeedAsync` and `GetCollectionAsync` work identically whether the target is a `Person` outbox or a `Group` community feed. The host app (Blazor) presents a single "feed" UI that switches between personal and community contexts.
- **Server-side Caching**:
  - `IServerCache` — composed from `ICache<T>` (in `Iris.Core`):
    - **Remote actor cache** — `Iri → Actor` (fetched from remote servers), TTL 1 hr. Reduces repeated fetches of the same remote actor during inbox processing and delivery.
    - **Remote key cache** — `Iri (keyId) → PublicKey`, TTL 1 hr. Avoids re-fetching public keys for signature verification.
    - **Collection page cache** — `Iri → OrderedCollectionPage`, TTL 5 min. Used when the server fetches remote collections (e.g. to populate a community feed from a followed remote community).
    - **WebFinger cache** — `resource → WebFingerResponse`, TTL 1 hr.
  - **Local data**: reads from `IActorStore`/`IActivityStore` are direct (in-memory or DB) — no additional cache layer needed for local data in the in-memory implementation. With a real DB backend, a query cache can be added at the persistence layer.
  - **Invalidation**: when a local actor's document changes (profile update, key rotation), the server invalidates its own cached entry. When a remote actor is updated (via `Update` activity in inbox), the remote actor cache entry is invalidated.
  - **`bypassCache` / `forceRefresh`**: all cached read paths accept a flag to skip the cache. Exposed in endpoints via `?refresh=true` query parameter (for debugging/admin) and in the client API via `bypassCache: true`.

### 4. `Iris.Server.InMemory` (net10.0)

- `InMemoryPersistenceProvider` — `ConcurrentDictionary`-backed implementations of all store interfaces (including `ICommunityStore`).
- `InMemoryDeliveryQueue` — `Channel<DeliveryJob>` with a background `IHostedService` worker.
- `InMemoryServerCache` — `MemoryCache<T>` implementations for all server cache types.
- Zero configuration; suitable for dev, tests, and small deployments.

### 5. `Iris.Client.Extensions` (optional, later phase)

- Specialized extensions for our client talking to *our* server:
  - `AddIrisClient(services, serverBaseUri)` — pre-configured `IHttpClientFactory` with proxy fallback pointed at our server.
  - `IrisSession` — manages login/identity selection, token storage (in Blazor: `LocalStorage`/`SessionStorage` via abstraction).
  - Rich-media helpers, notification polling, etc.

## Cross-Cutting Concerns

### Caching Strategy

Caching is applied at **three layers** to minimize network chatter:

| Layer | What's cached | Default TTL | Bypass |
|-------|--------------|-------------|--------|
| **Client** (Blazor / .NET app) | Actors, collection pages, WebFinger, remote public keys | 30s–5min (per type) | `bypassCache: true` on every API call |
| **Server** (our backend) | Remote actors, remote keys, remote collection pages, WebFinger | 5min–1hr (per type) | `?refresh=true` query param on endpoints |
| **Server → Client responses** | HTTP `Cache-Control` headers on actor/collection endpoints | `max-age=60, stale-while-revalidate=300` for actors; `max-age=30` for collection pages | `Cache-Control: no-cache` honored; `?refresh=true` bypasses |

**Principles:**
- **Short TTLs for volatile data** (collection pages: 30s), **longer TTLs for stable data** (actors: 5min client / 1hr server, keys: 1hr).
- **Stale-while-revalidate** on the client: if an entry is stale but within the stale window, return it immediately and refresh in the background. The user sees no latency; the next interaction gets fresh data.
- **Every read has an escape hatch**: `bypassCache: true` (client API), `?refresh=true` (server endpoints), `ICache.InvalidateAsync` (programmatic).
- **Cache keys are IRIs** — the natural identity of ActivityPub objects. No composite keys needed.
- **Invalidation on mutation**: posting to an outbox invalidates the outbox's first page. Receiving an `Update` activity invalidates the affected actor's cache entry. Key rotation invalidates the key cache.
- **No cache for private/authenticated data** served to the owner (e.g. the actor document with `privateKey` is always `Cache-Control: no-store`).

### HTTP Signatures

- Implement per [HTTP Signatures spec](https://datatracker.ietf.org/doc/html/draft-cavage-http-signatures-03) as used by ActivityPub.
- **Two signing profiles** (due to Blazor/WASM header restrictions):
  - **Client→Server** (restricted): `headers="(request-target) host date"` — only headers that browser `fetch`/XHR can set.
    - `Signature: keyId="https://example.com/actors/alice#key-1", algorithm="rsa-sha256", headers="(request-target) host date", signature="base64..."`
    - Signature base: `(request-target): post\nhost: example.com\ndate: Tue, 26 Aug 2026 12:00:00 GMT`
  - **Server→Server** (full): `headers="(request-target) host date digest content-type"` — includes body digest for POST integrity.
    - Signature base adds: `digest: sha-512=base64...\ncontent-type: application/activity+json`
- Both signing (client) and verification (server) live in `Iris.Core` (pure crypto, no HTTP) so they're testable and shareable.
- `ISignatureSigner` / `ISignatureVerifier` interfaces; default RSA + ECDSA implementations.
- The server's verifier **accepts both profiles** — it reconstructs the signature base from whatever `headers` list is declared in the `Signature` header, so it naturally handles both restricted and full sets.

### Key Model & Signing Identity

- **Per-actor keys**: each local actor has its own `KeyPair` (RSA-2048 or EC P-256). The actor's `publicKey` (JWK) is published in their actor document. The `privateKey` is only served to the authenticated owner.
- **System identity**: the server has a `SystemIdentity` (an `Application`-type actor) used for:
  - Server-to-server deliveries for **automated/system events** where no specific local actor is the semantic sender (e.g. server-level notifications, relay operations).
  - Signing proxied requests on behalf of clients when the client's own key is not suitable (e.g. the target server doesn't recognize the client's key).
  - NodeInfo / server-level documents.
- **General rule**: activities are signed by the **actor's identity** (the `actor` property on the activity matches the `keyId` in the signature). The system identity is the fallback for non-actor operations.
- Stored in `IKeyStore`; generated on first run if not provided (in-memory: ephemeral; real persistence: persisted).

### Proxied Request Fallback (detailed flow)

```
Blazor Client                Our Server                 Remote Server
     |                            |                            |
     |--- GET /actor ------------>|                            |
     |    (direct, signed w/      |                            |
     |     actor key, restricted  |                            |
     |     headers)               |                            |
     |<-- 401 / CORS error ------|                            |
     |                            |                            |
     |--- POST /ap/proxy/{t} ---->|                            |
     |    (Basic auth)            |--- GET /actor ------------>|
     |                            |    (signed w/ actor key,  |
     |                            |     full headers)         |
     |                            |<-- 200 actor doc ---------|
     |<-- 200 actor doc ---------|                            |
```

- `ProxyFallbackHandler` in the client detects failure modes and transparently retries via the proxy.
- The proxy request carries the client's Basic auth (v1) so the server can identify which actor is making the request and sign with **that actor's key** (not the system key). The system key is only used when no specific actor is associated.
- Security: proxy requires valid client auth (Basic in v1); target IRI is allowlisted to `application/activity+json` content types; rate-limited per actor identity.

## Testing Strategy

**Philosophy: integration-first, end-to-end.** We maximize coverage with a small number of high-fidelity integration tests that exercise real HTTP, real signing, real persistence, and real federation — instead of a large sprawl of isolated unit tests. Unit tests exist only where they add value that integration tests can't reach (pure crypto edge cases, IRI parsing, cache TTL/eviction logic).

### In-Process Multi-Instance Test Harness

- **`TestServer` harness** (in a shared `tests/Iris.Testing` project): spins up **multiple fully in-process `WebApplication` instances**, each with:
  - Its own `Iris.Server` pipeline + `Iris.Server.InMemory` persistence.
  - A **distinct hostname** from the `*.domain.local` range — **start with `a.domain.local` and `b.domain.local`** for basic federation; the harness scales to **N instances** (`a.domain.local`, `b.domain.local`, `c.domain.local`, …) for relay/fan-out scenarios. Each instance has its own system identity + key.
  - A real `HttpClient` wired to the instance's `TestServer`/Kestrel endpoint so requests go through the full HTTP stack (headers, content negotiation, signature validation, caching).
- **Instance-to-instance federation**: tests create actors on instance A, follow actors/communities on instance B, and assert that activities are delivered, signature-validated, stored, and visible in feeds/outboxes on the receiving instance. This proves **instance-to-instance compatibility** — the core property of a federated protocol.
- **N-instance relay/fan-out**: the harness is designed from the start to spin up **N servers** so we can test relay and fan-out topologies (one actor followed by many, a relay re-broadcasting, etc.) — not just pairwise federation.
- **Client against server**: the `Iris.Client` (including proxy fallback) is exercised against these live instances, including the Basic-auth → private-key → signed-request flow.
- **Distinct hostnames matter**: signature validation, WebFinger, IRI resolution, and cache keys are all hostname-sensitive. The harness guarantees each instance has a unique, resolvable hostname so these paths are genuinely exercised.

### Test Project Layout

```
tests/
├── Iris.Testing/                 shared harness: TestServer factory, multi-instance topology,
│                                 actor/credential fixtures, assertion helpers
├── Iris.Core.Tests/              focused unit tests ONLY for pure logic:
│                                 sign/verify round-trip (both profiles), tamper detection,
│                                 key generation, IRI helpers, cache TTL/eviction/stale-revalidate
├── Iris.Client.Tests/            integration: client ↔ live TestServer (auth flow, discovery,
│                                 paged enumeration, cache hit/bypass, proxy fallback)
└── Iris.Server.Tests/            integration: multi-instance federation (follow/accept/create/announce,
                                  community feed propagation, signature validation across instances,
                                  WebFinger/NodeInfo, cache refresh)
```

### Live Mastodon Compatibility Test (deferred — far later)

- **Deferred until instance-to-instance viability is first confirmed** with our own in-process servers. This is a downstream goal, not part of the near-term phases.
- A **separate, opt-in** integration suite (not part of the default `dotnet test` run) that:
  - Runs in a **fully isolated, routable Docker Compose environment**: our server instance + a **Dockerized Mastodon** (+ optional relay) on an internal network with routable hostnames.
  - Orchestrates Mastodon via its **admin/REST API** to create test accounts, posts, and follows.
  - Runs our Iris server instance against it: our instance follows a Mastodon account, receives its posts, and (where possible) posts to Mastodon and confirms delivery.
  - Asserts **server-to-external-server compatibility** — the ultimate interop proof.
- Gated behind an environment flag (e.g. `IRIS_MASTODON_TEST=1`) and the Docker Compose environment, so CI can run it as a dedicated job while local/dev runs skip it.

### Coverage Principle

- Every **phase** ships with the integration tests that prove its end-to-end behavior before it's marked done.
- Prefer one test that federates two instances over five tests that mock each layer.
- The harness is a first-class, maintained artifact — its ergonomics determine how much integration coverage we can afford to write.

## Spec Research

Before and during implementation, **research the ActivityPub specification** (and related specs) to ensure we understand all requirements, not just the ones we've already inferred:

- **ActivityPub** (W3C): actor types, activity/object vocab, inbox/outbox semantics, signature requirements, delivery & retry, shared inbox, WebFinger, NodeInfo.
- **ActivityStreams 2.0**: object/activity model, collections & pagination, `@context` handling.
- **HTTP Signatures** (draft-cavage-http-signatures-03): signature base construction, header sets, key types.
- **WebFinger** (RFC 8615) & **JRD** (RFC 7033): discovery & response format.
- **NodeInfo 2.0**: server metadata.
- Cross-check against **Mastodon / Pleroma / Lemmy** implementations for real-world conventions (content types, pagination defaults, error handling, `Cache-Control`).

Findings that change our assumptions get folded back into this plan (and the Resolved Decisions / Open Questions sections).

## Phased Plan

### Phase 0 — Scaffolding (this step)

- [ ] **Spec research pass** — read ActivityPub, ActivityStreams 2.0, HTTP Signatures, WebFinger/JRD, NodeInfo; note requirements & real-world conventions (see *Spec Research*). Fold findings into this plan.
- [ ] Create solution + all project files with correct TFM (**net10.0**) and package references.
- [ ] Add `KristofferStrube.ActivityStreams` package reference to `Iris.Core`.
- [ ] Set up `Directory.Build.props` (LangVersion, Nullable, TreatWarningsAsErrors, central package versions).
- [ ] Add test projects (xUnit) **including the shared `Iris.Testing` multi-instance `TestServer` harness** (distinct hostnames, in-memory persistence, real HTTP).
- [ ] Verify `dotnet build` succeeds on the empty skeleton.

### Phase 1 — Core: Identity, Keys, Signatures & Caching

- [ ] `Iri` value type + helpers (wraps `Uri`, `#Public` constant, inbox/outbox derivation).
- [ ] `IIdentity`, `SystemIdentity`, `KeyPair`, `KeyPairGenerator` (RSA-2048, EC P-256).
- [ ] `IKeyStore` interface.
- [ ] `HttpRequestMetadata` value type.
- [ ] `ISignatureSigner` / `ISignatureVerifier` + RSA/ECDSA implementations.
- [ ] `SigningProfile` enum (`ClientToServer` restricted / `ServerToServer` full).
- [ ] `ActivityJson` static helpers (pre-configured `JsonSerializerOptions` with ActivityStreams converters).
- [ ] `ICache<T>`, `CacheEntry<T>`, `MemoryCache<T>`, `CachePolicy` (in-memory, TTL, LRU eviction, stale-while-revalidate). TTLs **configurable** via options.
- [ ] **PEM private-key load/save** helpers (`RSA`/`ECDsa` ↔ PKCS#8 PEM) for the `privateKey` actor-doc property.
- [ ] Unit tests (pure logic only): sign/verify round-trip (both profiles), tamper detection, key generation, PEM round-trip, IRI helpers, cache TTL/eviction/stale-revalidate.

### Phase 2 — Client Library

- [ ] `IActivityPubClient` + `IActivityPubClientFactory` (operates on ActivityStreams types).
- [ ] `SigningHandler` (respects `SigningProfile`), `JsonLdHandler`, `RetryHandler`.
- [ ] `IClientAuthenticator` + `BasicAuthClientAuthenticator` (fetches actor doc + private key from our server).
- [ ] `IKeyProvider` (in-memory session key store).
- [ ] `IDiscoveryService` + WebFinger client.
- [ ] **Client caching**: `ActorCache`, `CollectionPageCache`, `WebFingerCache`, `KeyCache` (all with `bypassCache` support + stale-while-revalidate).
- [ ] **Rich paged collections**: `CollectionPage`, `IAsyncEnumerable<CollectionPage> GetCollectionAsync(...)`, `GetCollectionItemsAsync(...)` (flattened), `CollectionQuery` options. All collections share the same `limit`/`offset`-style shape.
- [ ] **Integration tests** (client ↔ live `TestServer`): signing, Basic-auth → private-key (PEM) flow, discovery, cache hit/bypass, paged enumeration (multi-page follow, early break).

### Phase 3 — Server Foundation

- [ ] Persistence interfaces (`IActorStore`, `IActivityStore`, `IFollowStore`, `IObjectStore`, `IKeyStore`, `ICommunityStore`).
- [ ] `Iris.Server.InMemory` implementations (including `InMemoryServerCache`).
- [ ] `AddActivityPubServer()` + `MapActivityPubEndpoints()` — **versioned route prefix** (`/ap/v1`) + `Iris-Version` meta header.
- [ ] Actor document endpoint **with conditional `privateKey` inclusion** (Basic auth → `IActorCredentialValidator`).
- [ ] WebFinger, NodeInfo endpoints.
- [ ] **Server caching**: remote actor cache, remote key cache, collection page cache, WebFinger cache. `?refresh=true` bypass on all cached endpoints. `Cache-Control` headers on responses. TTLs configurable.
- [ ] **Integration tests** (live `TestServer`): public actor doc, authenticated actor doc (with PEM `privateKey`), WebFinger, cache hit/miss/refresh.

### Phase 4 — Inbox & Delivery

- [ ] `SignatureValidationMiddleware` + `ISignatureValidator` (accepts both signing profiles).
- [ ] `IInboxProcessor` pipeline + `IActivityHandler<T>` registration.
- [ ] `IDeliveryService` + `IDeliveryQueue` (in-memory `Channel<T>`).
- [ ] Delivery signs with the **actor's key** (system key for automated events).
- [ ] Follow/Accept/Reject flow end-to-end.
- [ ] **Integration tests**: two in-process `TestServer` instances (distinct hostnames) exchanging follows with full signature validation — the first true instance-to-instance federation test.

### Phase 5 — Community / Group Support

- [ ] `ICommunityStore` + in-memory implementation.
- [ ] Community endpoints: `GET /c/{name}`, `GET /c/{name}/members`, `GET /c/{name}/feed`, `POST /c/{name}/inbox`.
- [ ] `ICommunityFeedService` — unified feed (local member posts + followed community content).
- [ ] `CommunityInboxHandler` — stores content from followed remote communities for feed propagation.
- [ ] Community following: a `Group` actor follows another community/actor; content is received and propagated to local members.
- [ ] Client: `GetCommunityFeedAsync(Iri communityId, FeedFilter?)` — works identically to personal feeds.
- [ ] **Specialized collections**: `/c/{name}/feed`, `/c/{name}/search` using the shared `limit`/`offset` shape; **`iris:capabilities`** property (configurable namespace) on the community/actor document declares available features for client discovery.
- [ ] **Integration tests**: create community on instance A → follow remote community on instance B → receive content → appears in community feed (cross-instance).

### Phase 6 — Proxy Fallback

- [ ] Server `POST /ap/proxy/{target}` endpoint (Basic auth → identify actor → sign with actor's key → forward).
- [ ] Client `ProxyFallbackHandler` (detects CORS/401/403 → retries via proxy with Basic auth).
- [ ] Rate limiting + target allowlist.
- [ ] **Integration tests**: simulated CORS/401 failure → proxy path → remote `TestServer` instance receives a correctly signed request.

### Phase 7 — Blazor Client Extensions & Samples

- [ ] `Iris.Client.Extensions` package.
- [ ] `IrisSession` (identity selection, in-memory key persistence for session lifetime).
- [ ] `AddIrisClient(services, serverBaseUri)` — pre-configured pipeline with proxy fallback.
- [ ] `SampleServer` app (ASP.NET Core + Iris.Server + in-memory persistence + Basic auth + a sample community).
- [ ] `SampleBlazorClient` app (WASM + Iris.Client — personal feed + community feed UI).
- [ ] End-to-end: Blazor client authenticates → gets key → signs requests → community feed → proxy fallback to remote server.

### Phase 8 — Live Mastodon Compatibility Test (deferred — after instance-to-instance viability)

- [ ] **Fully isolated, routable Docker Compose environment**: our server instance + Dockerized Mastodon (+ optional relay) on an internal network with routable hostnames.
- [ ] Opt-in integration suite (gated by `IRIS_MASTODON_TEST=1` + the Compose environment).
- [ ] Orchestrate Mastodon via its **admin/REST API**: create test accounts, posts, and follows.
- [ ] Run our Iris server instance against Mastodon: follow a Mastodon account → receive & store its posts; post from Iris → confirm Mastodon receives it.
- [ ] Assert **server-to-external-server compatibility** (signatures, content types, pagination, WebFinger, delivery).
- [ ] Wire as a dedicated CI job; skip in local/dev runs.

### Phase 9+ (abstract, to be expanded later)

- **Auth upgrade**: replace Basic auth with OAuth2 bearer tokens or a dedicated key-exchange endpoint. `IActorCredentialValidator` makes this a drop-in swap.
- **Real persistence**: EF Core / PostgreSQL implementation of `IPersistenceProvider` (including `ICommunityStore`).
- **Delivery at scale**: background queue (RabbitMQ/Redis), parallel delivery, fan-out for large follower sets.
- **SharedInbox / Relay** support.
- **Community features**: moderation (hide/remove posts), community tags/subscriptions, cross-community search, community-level blocking.
- **Caching at scale**: distributed cache (Redis) implementation of `ICache<T>` for multi-instance server deployments.
- **Transport security hardening**: key rotation, `keyDocuments`, multi-key actors, key expiry.
- **Federation testing**: interop with Mastodon/Pleroma/Lemmy test suites.
- **Observability**: OpenTelemetry spans for delivery, metrics for inbox throughput, cache hit-rate dashboards.
- **API surface review**: stabilize `Iris.Core`/`Iris.Client` for 1.0.

## Resolved Decisions

1. **Private key format in actor doc**: **PEM**. The `privateKey` property on the authenticated actor document carries a PEM-encoded private key (PKCS#8 for RSA, SEC1/PKCS#8 for EC). PEM loads directly into `RSA`/`ECDsa` via `ImportPkcs8PrivateKey`/`ImportFromPem`-style helpers — no JWK→parameter loader needed. The public key remains JWK in the standard `publicKey` field (ActivityPub convention).
9. **Configurable namespace base**: the `iris:` namespace IRI is **configurable per-deployment** (an option, e.g. `options.NamespaceIri`). Forks may extend the namespace with additional terms/capabilities later without breaking the base. The default is a canonical Iris IRI; a fork overrides it.
10. **API versioning via route prefix**: versioning is an **early, first-class design decision** using a **route prefix** (e.g. `/ap/v1/...`) rather than a post-hoc change. A version header (e.g. `Iris-Version`) is also emitted as **meta information** for observability/interop, but the route prefix is the authoritative versioning mechanism. New major versions add a new prefix; existing prefixes stay stable.
11. **`iris:capabilities` on collections**: a custom **`iris:capabilities`** property (in the configurable namespace) on a collection/actor/community document **declares what is available** on that collection (e.g. `search`, `sort`, `filter`, `feed`). This is the discovery mechanism for specialized collections — a client reads `iris:capabilities` to know which extended query params / sub-collections the server supports.
12. **Mastodon live test — deferred**: the live Mastodon compatibility suite is **saved for far later**, after instance-to-instance viability is first confirmed with our own in-process servers. When we get there, it runs against a **Dockerized Mastodon** in a **fully isolated, routable Docker-composed test environment** (our server instance + Mastodon + any relay on an internal network with routable hostnames).
13. **Multi-instance test topology**: **start with 2 servers** for basic federation, using simple hostnames **`a.domain.local`** and **`b.domain.local`**. **Plan for N servers** to support relay/fan-out scenarios (the harness must scale to N instances with distinct `*.domain.local` hostnames).
2. **`privateKey` property name**: **`privateKey`** confirmed. Non-standard extension of the actor document, served only to authenticated owners with `Cache-Control: no-store`.
3. **Pagination**: `OrderedCollectionPage` with `next`/`prev`, **default page size 20**.
4. **Content types**: **flexible** — produce `application/activity+json`; accept `application/activity+json` and `application/ld+json` on inbound. Content negotiation is lenient, not locked to a single type.
5. **Client key lifetime**: **in-memory for v1** (lost on page refresh; user re-authenticates — acceptable for Basic auth). Revisit with OAuth2.
6. **Collection & feed format**: **all collections share one `limit`/`offset`-style pagination shape** (backed by `OrderedCollectionPage` `next`/`prev`). Rather than inventing a custom feed format, we **extend the ActivityStreams schema** to support search/sort parameters, and/or expose **specialized collections** off actors/communities (e.g. `/c/{name}/feed`, `/c/{name}/search`) that implement specialized searching/sorting. Our own namespace (e.g. `iris:` terms) acts as the **capability indicator** — a client that sees an `iris:`-namespaced collection link knows the server supports the extended search/sort semantics. Standard `OrderedCollectionPage` remains the wire format for federation compatibility.
7. **Community propagation model**: **(b) local-only** for v1 — the community feed is a local aggregation; federation happens at the community-follow level, not per-post.
8. **Cache TTLs**: **configurable per-deployment** via `CachePolicy`/options. Defaults as listed (actors 5min client / 1hr server, pages 30s, keys 1hr) are the starting point.

## Open Questions (to resolve as we go)

1. **Canonical default namespace IRI**: pick the default `iris:` namespace IRI (e.g. `https://iris.example/ns#`) used when a deployment doesn't override it. The base is configurable (see Resolved #9); this is just the out-of-the-box default.
2. **Route prefix shape**: confirm the exact prefix convention (e.g. `/ap/v1/...` vs `/v1/ap/...`) and whether the unversioned root (`/ap/...`) should alias to the latest version for convenience/back-compat.
3. **`iris:capabilities` vocabulary**: define the concrete capability terms (e.g. `search`, `sort`, `filter`, `feed`, `members`) and their allowed values so clients can branch on them predictably.
4. **Mastodon Docker composition** (when we get there): the exact Docker Compose topology — our server + Mastodon + optional relay, internal routable network, hostname assignments, and which Mastodon admin/REST endpoints to orchestrate (account creation, posting, following).

## Conventions

- **TFM: net10.0** for all projects (client, server, tests, samples).
- C# latest, nullable enabled, file-scoped namespaces.
- `System.Text.Json` exclusively (no Newtonsoft).
- ActivityStream/ActivityPub types come from `KristofferStrube.ActivityStreams` — we do NOT re-implement them. Iris types compose/extend where needed.
- **Testing: integration-first.** xUnit; the shared `Iris.Testing` harness spins up multiple in-process `TestServer` instances with distinct `*.domain.local` hostnames (start with `a.domain.local`/`b.domain.local`, scale to N for relay/fan-out) for end-to-end federation tests. Unit tests are reserved for pure logic (crypto, IRI, cache). `Microsoft.AspNetCore.Mvc.Testing` / Kestrel for real HTTP. A separate opt-in Mastodon live-compat suite (Phase 8, deferred) runs in an isolated Docker Compose environment gated behind an env flag.
- **Versioning**: public API is versioned via a **route prefix** (e.g. `/ap/v1/...`) — an early design decision. A version header (e.g. `Iris-Version`) is emitted as meta information. New capabilities are signaled via `iris:`-namespaced terms (configurable namespace base), never by breaking existing endpoints.
- Central package management (`Directory.Packages.props`).
- `Iris.Core` depends on `KristofferStrube.ActivityStreams` + BCL only.
- `Iris.Client` depends on `Iris.Core` + BCL.
- `Iris.Server` depends on `Iris.Core` + `Iris.Client` + ASP.NET Core.
- `Iris.Server.InMemory` depends on `Iris.Server`.
- **Caching**: all cached reads expose a `bypassCache` / `forceRefresh` parameter. No cached path is opaque.
