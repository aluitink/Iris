# Iris — Roadmap

> Part of the [Iris plan](../PLAN.md). See also [Architecture](ARCHITECTURE.md), [Projects](PROJECTS.md), [Testing](TESTING.md), [Coding Style](CODING_STYLE.md).

## Phased Plan

### Phase 0 — Scaffolding (this step)

- [ ] **Spec research pass** — read ActivityPub, ActivityStreams 2.0, HTTP Signatures, WebFinger/JRD, NodeInfo; note requirements & real-world conventions (see [Spec Research](ARCHITECTURE.md#spec-research)). Fold findings into this plan.
  - `remaining:` findings not yet captured/folded back; the research *directive* is in place but no concrete findings have been recorded. Carry forward into Phase 1 (signing/IRI work depends on it).
- [x] Create solution + all project files with correct TFM (**net10.0**) and package references.
- [x] Add `KristofferStrube.ActivityStreams` package reference to `Iris.Core`.
- [x] Set up `Directory.Build.props` (LangVersion, Nullable, TreatWarningsAsErrors, central package versions).
- [x] Add test projects (xUnit) **including the shared `Iris.Testing` multi-instance `TestServer` harness** (distinct hostnames, in-memory persistence, real HTTP).
- [x] Verify `dotnet build` succeeds on the empty skeleton.
  - `remaining:` `dotnet build Iris.slnx` is clean (0 warnings/0 errors) and `dotnet test Iris.slnx` is green (7/7). A *bare* `dotnet build` in the repo root is blocked by MSB1011 (two root project/solution files: `Iris.slnx` + a stray scratch `inspect.csproj`). `rm` is permission-blocked, so the stray root `Program.cs`/`inspect.csproj`/`packages.lock.json` scratch files remain; delete them manually to restore a bare build.

### Phase 1 — Core: Identity, Keys, Signatures & Caching

- [x] `Iri` value type + helpers (wraps `Uri`, `#Public` constant, inbox/outbox derivation).
  - Done: `Iri` (record struct), `Iri.Public`, `TryParse`, and `IriExtensions` (`InboxOf`/`OutboxOf`/`FollowersOf`/`FollowingOf` + library `string?`/`Uri?` ↔ `Iri` boundary conversions). Unit-tested incl. relative-vs-absolute handling.
- [x] `IIdentity`, `SystemIdentity`, `KeyPair`, `KeyPairGenerator` (RSA-2048, EC P-256).
  - Done: `KeyAlgorithm` (Rsa/EcP256), `KeyPair` (Sign/Verify SHA-256, ExportPrivateKeyPem, GetPublicJwk, RFC 7638 thumbprint, FromPem), `KeyPairGenerator` (RSA-2048 / EC P-256), `IIdentity` + `SystemIdentity` record.
- [x] `IKeyStore` interface.
  - Done: `IKeyStore` (put/try-get/remove) + `InMemoryKeyStore` (ephemeral, disposes evicted keys).
- [x] `HttpRequestMetadata` value type.
  - Done: `HttpRequestMetadata` (HTTP-agnostic snapshot; value-based equality; case-insensitive header lookup; `With()` for partial copies).
- [x] `ISignatureSigner` / `ISignatureVerifier` + RSA/ECDSA implementations.
  - Done: `Signatures` (shared base/digest/algorithm-label/`Signature`-header constants), `SignatureHeader` (parse/format), `HttpSignatureSigner` / `HttpSignatureVerifier` (RSA + ECDSA). Keys are **borrowed** from the `IKeyStore` (store owns lifetime).
- [x] `SigningProfile` enum (`ClientToServer` restricted / `ServerToServer` full).
  - Done: `ClientToServer` = `(request-target) host date`; `ServerToServer` = `+ digest content-type`. Verifier reconstructs the base from the declared `headers` list, so it accepts both.
- [x] `ActivityJson` static helpers (pre-configured `JsonSerializerOptions` with ActivityStreams converters).
  - Done: single `JsonSerializerOptions` (registers the library's `ObjectOrLinkConverter`, `WhenWritingDefault`, no naming policy); content-type constants; `Serialize`/`Deserialize` overloads. Unit-tested against the wire format.
- [x] `ICache<T>`, `CacheEntry<T>`, `MemoryCache<T>`, `CachePolicy` (in-memory, TTL, LRU eviction, stale-while-revalidate). TTLs **configurable** via options.
  - Done: `CachePolicy` (record struct + validated defaults: Actor 5m, CollectionPage 30s, Key 1h, WebFinger 15m), `CacheState` (Fresh/Stale/Expired), `CacheEntry<TValue>` (value + createdAt + captured policy; `GetState(now)`), `ICache<TValue>` (Get/TryGetEntry/Put/Invalidate/Count, keyed by `Iri`, clock injected), `MemoryCache<TValue>` (thread-safe LRU, bounded capacity, opportunistic expired-eviction, stale-while-revalidate), `CachedValue<TValue>` (lookup result), `CacheExtensions` (`Lookup`/`GetOrAdd`).
- [x] **PEM private-key load/save** helpers (`RSA`/`ECDsa` ↔ PKCS#8 PEM) for the `privateKey` actor-doc property.
  - Done: `KeyPem.Load`/`KeyPem.Save` over `KeyPair.FromPem`/`ExportPrivateKeyPem`. Round-trip tested for both algorithms.
- [x] Unit tests (pure logic only): sign/verify round-trip (both profiles), tamper detection, key generation, PEM round-trip, IRI helpers, cache TTL/eviction/stale-revalidate.
  - Done: key generation, key sign/verify round-trip + tamper (both algos), PEM round-trip, IRI helpers, **full HTTP-signature sign/verify per `SigningProfile`** (both algos x both profiles, signature-base spec examples, digest, tamper detection, unknown-key/malformed-header), and **cache TTL/stale-while-revalidate/LRU eviction/invalidation** (incl. exact-boundary behavior and expired-eviction on write). Phase 1 complete.

### Phase 2 — Client Library

- [x] `IActivityPubClient` + `IActivityPubClientFactory` (operates on ActivityStreams types).
  - Done: `IActivityPubClient` (`GetObjectAsync` → `IObject`, `GetActorAsync` → `Actor` via pattern-match cast, `DeliverAsync` → status) implemented by `ActivityPubClient`; `IActivityPubClientFactory`/`ActivityPubClientFactory` wire a `SigningHandler` (signer + key provider over the caller's transport) into an owned `HttpClient`. `IActivityPubClient` extends `IDisposable`. `remaining:` none for this bullet (caching, paged collections, `JsonLd`/`Retry` handlers tracked separately below).
- [x] `SigningHandler` (respects `SigningProfile`).
   - Done: `SigningHandler` (`DelegatingHandler`) adds `Date` + `Signature`; `ClientToServer` for bodyless GETs, `ServerToServer` (digest+content-type) for body POSTs; derives host from the request URI when no `Host` header is set. Round-trip verified against `HttpSignatureVerifier`.
   - Done: `JsonLdHandler` (`DelegatingHandler`, content negotiation per Decision #4) — bodyless requests get `Accept: application/activity+json, application/ld+json`; body requests get `application/activity+json` as `Content-Type` when unset. `RetryHandler` (`DelegatingHandler`) — retries only idempotent requests (GET/HEAD/OPTIONS), never POST/PUT/DELETE (avoids double-delivery); retries on 429/5xx + transient `HttpRequestException`; honors `Retry-After` (delta-seconds); exponential backoff (base 250ms, doubling) with up to 100% jitter; injectable delay + jitter source for deterministic tests; default budget 3 attempts. Factory pipeline order: `RetryHandler` → `JsonLdHandler` → `SigningHandler` → transport (retry outermost so it replays the signed request; JsonLd sets headers before signing). `ActivityPubClientOptions.EnableRetry` (default true) + `MaxRetryAttempts` (default 3). 12 handler tests. `remaining:` none.
- [x] `IClientAuthenticator` + `BasicAuthClientAuthenticator` (fetches actor doc + private key from our server).
  - Done: `IClientAuthenticator.AuthenticateAsync` (Basic-auth → owner actor doc + loaded `KeyPair`); `BasicAuthClientAuthenticator` (GET with `Authorization: Basic`, reads owner-only `privateKey` PKCS#8 PEM extension, loads via `KeyPem`; keyId from `publicKey.id` extension else actor IRI); `AuthenticatedActor` record. `remaining:` OAuth2 bearer / key-exchange authenticator (Phase 9+).
- [x] `IKeyProvider` (in-memory session key store).
  - Done: `IKeyProvider` (resolve signing `IIdentity` from an actor IRI) + `InMemoryKeyProvider` (actor→key map over `IKeyStore`; keys borrowed, never disposed).
- [x] `IDiscoveryService` + WebFinger client.
  - Done: `WebFingerClient` (RFC 8410 lookup on the account's own host, `self`-link resolution, `acct:` normalization, graceful null on 404/parse failure) + `IDiscoveryService`/`WebFingerDiscoveryService`.
- [x] **Client caching**: `ActorCache`, `CollectionPageCache`, `WebFingerCache`, `KeyCache` (all with `bypassCache` support + stale-while-revalidate).
  - Done: `CachingClientCache<TValue>` (generic async read-through engine over `ICache<TValue>`: `bypassCache` skips the read but writes back; stale-while-revalidate serves a stale entry immediately and refreshes in the foreground; an absent/null result is never cached so 404s retry). Four concrete caches: `ActorCache` (`IObject`, `CachePolicy.Actor` 5m), `CollectionPageCache` (`IObject`, `CachePolicy.CollectionPage` 30s), `WebFingerCache` (`Iri` via `WebFingerHit`, `CachePolicy.WebFinger` 15m), `KeyCache` (`JwkKey`, `CachePolicy.Key` 1h). `JwkKey` (JWK JSON + algorithm label) and `WebFingerHit` (account + actor IRI) records added. **Wired into the call paths:** `ClientCaches` (record: `Actors`/`CollectionPages`/`WebFinger`) threaded through `ActivityPubClientOptions.Caches` + the factory; `ActivityPubClient.GetObjectAsync`/`GetActorAsync` read through `ActorCache`, `GetCollectionAsync` reads each page through `CollectionPageCache` (honoring `CollectionQuery.BypassCache`); `WebFingerClient.ResolveActorAsync` reads through `WebFingerCache` (keyed by the normalized `acct:` subject). A null cache = no caching (straight to network). 15 cache unit tests + 8 cache-wiring unit tests. `remaining:` `KeyCache` wiring (client-side inbound-signature verification is Phase 3 server-side; a client fetch-remote-public-key path is a follow-up).
- [x] **Rich paged collections**: `CollectionPage`, `IAsyncEnumerable<CollectionPage> GetCollectionAsync(...)`, `GetCollectionItemsAsync(...)` (flattened), `CollectionQuery` options. All collections share the same `limit`/`offset`-style shape.
  - Done: `CollectionPage` (Iris wrapper — contains `OrderedCollectionPage`, flattened `IReadOnlyList<IObjectOrLink> Items`, `Iri? NextPage`/`Iri? PrevPage`, `int? TotalItems`, `Iri? PageId`, `IsLastPage`); `CollectionQuery` (record: `Limit`, `BypassCache`); `IActivityPubClient.GetCollectionAsync` (follow the collection's `first` link — or use the fetched object directly if it is itself a page — then follow `next` links, yielding `CollectionPage` in order; stops at `Limit` or the last page; yields nothing on 404 / non-page) and `GetCollectionItemsAsync` (flattens per-page `Items` across pages in order, respecting `Limit` *within* a page). `Next`/`Prev`/`First` (`ICollectionPageOrLink`/`ICollectionOrLink`) resolve to `Iri` via `TryGetIri` (`ILink.Href` or `IObject.Id`). Items deserialize from the `items` JSON key (the library's `Items` maps to `items`, not `orderedItems` — Resolved Decision #22). 8 unit tests (multi-page follow, item deserialization, limit on pages & items, 404 yields nothing, `IsLastPage`). `remaining:` `offset`-style starting offset (v1 follows from `first`; a start offset is a follow-up), and wiring `BypassCache`/`CollectionPageCache` into the fetch path (tracked under the integration-tests bullet).
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
2. **`privateKey` property name**: **`privateKey`** confirmed. Non-standard extension of the actor document, served only to authenticated owners with `Cache-Control: no-store`.
3. **Pagination**: `OrderedCollectionPage` with `next`/`prev`, **default page size 20**.
4. **Content types**: **flexible** — produce `application/activity+json`; accept `application/activity+json` and `application/ld+json` on inbound. Content negotiation is lenient, not locked to a single type.
5. **Client key lifetime**: **in-memory for v1** (lost on page refresh; user re-authenticates — acceptable for Basic auth). Revisit with OAuth2.
6. **Collection & feed format**: **all collections share one `limit`/`offset`-style pagination shape** (backed by `OrderedCollectionPage` `next`/`prev`). Rather than invent a custom feed format, we **extend the ActivityStreams schema** to support search/sort parameters, and/or expose **specialized collections** off actors/communities (e.g. `/c/{name}/feed`, `/c/{name}/search`) that implement specialized searching/sorting. Our own namespace (e.g. `iris:` terms) acts as the **capability indicator** — a client that sees an `iris:`-namespaced collection link knows the server supports the extended search/sort semantics. Standard `OrderedCollectionPage` remains the wire format for federation compatibility.
7. **Community propagation model**: **(b) local-only** for v1 — the community feed is a local aggregation; federation happens at the community-follow level, not per-post.
8. **Cache TTLs**: **configurable per-deployment** via `CachePolicy`/options. Defaults as listed (actors 5min client / 1hr server, pages 30s, keys 1hr) are the starting point.
9. **Configurable namespace base**: the `iris:` namespace IRI is **configurable per-deployment** (an option, e.g. `options.NamespaceIri`). Forks may extend the namespace with additional terms/capabilities later without breaking the base. The default is a canonical Iris IRI; a fork overrides it.
10. **API versioning via route prefix**: versioning is an **early, first-class design decision** using a **route prefix** (e.g. `/ap/v1/...`) rather than a post-hoc change. A version header (e.g. `Iris-Version`) is also emitted as **meta information** for observability/interop, but the route prefix is the authoritative versioning mechanism. New major versions add a new prefix; existing prefixes stay stable.
11. **`iris:capabilities` on collections**: a custom **`iris:capabilities`** property (in the configurable namespace) on a collection/actor/community document **declares what is available** on that collection (e.g. `search`, `sort`, `filter`, `feed`). This is the discovery mechanism for specialized collections — a client reads `iris:capabilities` to know which extended query params / sub-collections the server supports.
12. **Mastodon live test — deferred**: the live Mastodon compatibility suite is **saved for far later**, after instance-to-instance viability is first confirmed with our own in-process servers. When we get there, it runs against a **Dockerized Mastodon** in a **fully isolated, routable Docker-composed test environment** (our server instance + Mastodon + any relay on an internal network with routable hostnames).
13. **Multi-instance test topology**: **start with 2 servers** for basic federation, using simple hostnames **`a.domain.local`** and **`b.domain.local`**. **Plan for N servers** to support relay/fan-out scenarios (the harness must scale to N instances with distinct `*.domain.local` hostnames).
14. **Key lifetime in signing**: a `KeyPair` wraps a non-clonable `AsymmetricAlgorithm`, so it **cannot be safely shared by reference across independent owners**. The `IKeyStore` is the single owner; `HttpSignatureSigner`/`HttpSignatureVerifier` **borrow** the key (never dispose it). A `KeyPair` created outside a store (e.g. by `KeyPairGenerator`) must be disposed by its creator.
15. **Algorithm label placement**: the `algorithm` value (`rsa-sha256` / `ecdsa-p256-sha256`) is carried in the `Signature` header, **not** folded into the signature base (which is built only from the declared `headers` list, per draft-cavage-03). Verification is by the key's actual algorithm; the label is informational and must match the key type.
16. **Cache clock injection**: `ICache<TValue>`/`MemoryCache<TValue>` take an explicit `nowUtc` on read/write (and an injectable `clock` on the constructor) rather than calling `DateTime.UtcNow` internally. This makes TTL / stale-while-revalidate / LRU-eviction **deterministic in unit tests** (no sleeps). The default `clock` is `DateTime.UtcNow`, so production callers get real time.
17. **Cache eviction policy**: `MemoryCache<TValue>` is a **bounded LRU** (capacity default 1024). Expired entries are evicted **opportunistically on write** (best-effort pass from the LRU end); stale entries are never evicted by time alone — they are served (stale-while-revalidate) until they cross the `StaleFor` window, at which point the next read returns a miss and evicts them.
18. **WebFinger base URL**: the WebFinger query is issued against the **account's own host** (derived from the `acct:` URI, e.g. `https://b.domain.local/.well-known/webfinger?resource=acct:bob@b.domain.local`), *not* the `HttpClient`'s `BaseAddress`. This matches the RFC 8410 / ActivityPub §3 rule that WebFinger is resolved on the domain that owns the account, and keeps the client correct for multi-domain federation without requiring a pre-set base address.
19. **Signing host derivation**: when an outgoing `HttpRequestMessage` has no explicit `Host` header, the `SigningHandler` derives the `host` signature component from the request URI's authority. An explicit `Host` header (virtual hosts / SNI) always takes precedence.
20. **Key algorithm round-trip**: both RSA and EC private keys are exported as identical PKCS#8 `-----BEGIN PRIVATE KEY-----` PEM (`.ExportPkcs8PrivateKeyPem()`), so the algorithm **cannot be inferred from the PEM header**. The actor document therefore carries a `keyAlgorithm` extension field (values `rsa` / `ecdsa-p256`) alongside `privateKey`; `BasicAuthClientAuthenticator` reads it to load the key with the correct `KeyAlgorithm`, defaulting to RSA when absent. (A deployment that exports EC as SEC1 `-----BEGIN EC PRIVATE KEY-----` would still be mis-detected — Iris standardizes on PKCS#8 + the explicit `keyAlgorithm` field.)
21. **Client `KeyCache` value type**: the **client** `KeyCache` maps `Iri (keyId) → JwkKey` (JWK JSON + algorithm label), *not* a `KeyPair`. A client only ever holds the **public** key material it fetched from a remote actor's `publicKey` link (needed to verify inbound signatures); it never has the private half, so caching a full `KeyPair` would be wrong. (The **server** key cache, Phase 3, caches over `IKeyStore`/`KeyPair` for its own actors.) `JwkKey` is a small record carrying the raw JWK JSON plus the `Signature`-header algorithm label, with `ToElement()` for parsing.
22. **`OrderedCollectionPage` items JSON key**: the `KristofferStrube.ActivityStreams` library deserializes `OrderedCollectionPage.Items` from the **`items`** JSON key (not `orderedItems`). The library's `Items` property carries a `[JsonPropertyName("items")]` mapping. Iris collection documents therefore emit/expect **`items`** on pages (the library's wire form). The ActivityStreams spec's `orderedItems` is *not* what this library reads — using it yields an empty `Items`. (If a remote federated server emits `orderedItems` instead, the client will see zero items; this is a known interop gap to revisit if it bites in live federation testing.)
23. **Client retry policy & pipeline order**: the client retries **only idempotent** requests (GET/HEAD/OPTIONS) — never POST/PUT/DELETE — because replaying an activity `DeliverAsync`/`Follow`/`Accept` would double-post. Retriable conditions: HTTP 429/500/502/503/504 and transient `HttpRequestException` (connection reset / timeout). `Retry-After` (delta-seconds) is honored when present; otherwise exponential backoff (base 250ms, doubling) with up to 100% additive jitter. Default budget is 3 total attempts, configurable via `ActivityPubClientOptions.MaxRetryAttempts`; `EnableRetry` (default true) toggles the handler. **Pipeline order** is `RetryHandler` → `JsonLdHandler` → `SigningHandler` → transport: retry is outermost so it replays the *fully signed* request (a fresh signature is produced each attempt); `JsonLdHandler` sets `Accept`/`Content-Type` *before* `SigningHandler` reads the content type into the signature base.

## Open Questions (to resolve as we go)

1. **Canonical default namespace IRI**: pick the default `iris:` namespace IRI (e.g. `https://iris.example/ns#`) used when a deployment doesn't override it. The base is configurable (see Resolved #9); this is just the out-of-the-box default.
2. **Route prefix shape**: confirm the exact prefix convention (e.g. `/ap/v1/...` vs `/v1/ap/...`) and whether the unversioned root (`/ap/...`) should alias to the latest version for convenience/back-compat.
3. **`iris:capabilities` vocabulary**: define the concrete capability terms (e.g. `search`, `sort`, `filter`, `feed`, `members`) and their allowed values so clients can branch on them predictably.
4. **Mastodon Docker composition** (when we get there): the exact Docker Compose topology — our server + Mastodon + optional relay, internal routable network, hostname assignments, and which Mastodon admin/REST endpoints to orchestrate (account creation, posting, following).
