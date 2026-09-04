# Iris — Project Details

> Part of the [Iris plan](../../PLAN.md). See also [Architecture](ARCHITECTURE.md), [Testing](TESTING.md), [Phase Ledger](../ROADMAP.md), [Coding Style](CODING_STYLE.md).

Per-project breakdown. All projects target **net10.0**. Conventions for working with the 3rd-party `KristofferStrube.ActivityStreams` types are in [Coding Style](CODING_STYLE.md).

## 1. `Iris.Core` (net10.0)

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

## 2. `Iris.Client` (net10.0)

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
  - `BasicAuthClientAuthenticator` (v1) — sends `Authorization: Basic base64(user:pass)` to `GET /{actorHandle}` on our server. The server, upon successful auth, returns the actor document **with an additional `privateKey` field** (PEM) alongside the normal `publicKey`. The client extracts and holds the private key in memory.
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

## 3. `Iris.Server` (net10.0)

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
  - `MapActivityPubEndpoints()` — registers (all under the **versioned route prefix**, e.g. `/ap/v1/...`; the prefix is an early design decision, see [Resolved #10](ROADMAP.md#resolved-decisions)):
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
  - The private key is served as a PEM (PKCS#8) in a `privateKey` property on the actor document. This is **only** included when auth succeeds.
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

## 4. `Iris.Server.InMemory` (net10.0)

- `InMemoryPersistenceProvider` — `ConcurrentDictionary`-backed implementations of all store interfaces (including `ICommunityStore`).
- `InMemoryDeliveryQueue` — `Channel<DeliveryJob>` with a background `IHostedService` worker.
- `InMemoryServerCache` — `MemoryCache<T>` implementations for all server cache types.
- Zero configuration; suitable for dev, tests, and small deployments.

## 5. `Iris.Client.Extensions` (optional, later phase)

- Specialized extensions for our client talking to *our* server:
  - `AddIrisClient(services, serverBaseUri)` — pre-configured `IHttpClientFactory` with proxy fallback pointed at our server.
  - `IrisSession` — manages login/identity selection, token storage (in Blazor: `LocalStorage`/`SessionStorage` via abstraction).
  - Rich-media helpers, notification polling, etc.
