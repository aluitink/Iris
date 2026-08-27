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
- [ ] `ICache<T>`, `CacheEntry<T>`, `MemoryCache<T>`, `CachePolicy` (in-memory, TTL, LRU eviction, stale-while-revalidate). TTLs **configurable** via options.
- [x] **PEM private-key load/save** helpers (`RSA`/`ECDsa` ↔ PKCS#8 PEM) for the `privateKey` actor-doc property.
  - Done: `KeyPem.Load`/`KeyPem.Save` over `KeyPair.FromPem`/`ExportPrivateKeyPem`. Round-trip tested for both algorithms.
- [ ] Unit tests (pure logic only): sign/verify round-trip (both profiles), tamper detection, key generation, PEM round-trip, IRI helpers, cache TTL/eviction/stale-revalidate.
  - Done so far: key generation, key sign/verify round-trip + tamper (both algos), PEM round-trip, IRI helpers, and **full HTTP-signature sign/verify per `SigningProfile`** (both algos x both profiles, signature-base spec examples, digest, tamper detection, unknown-key/malformed-header). Remaining: cache TTL/eviction/stale-revalidate (with the caching slice).

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

## Open Questions (to resolve as we go)

1. **Canonical default namespace IRI**: pick the default `iris:` namespace IRI (e.g. `https://iris.example/ns#`) used when a deployment doesn't override it. The base is configurable (see Resolved #9); this is just the out-of-the-box default.
2. **Route prefix shape**: confirm the exact prefix convention (e.g. `/ap/v1/...` vs `/v1/ap/...`) and whether the unversioned root (`/ap/...`) should alias to the latest version for convenience/back-compat.
3. **`iris:capabilities` vocabulary**: define the concrete capability terms (e.g. `search`, `sort`, `filter`, `feed`, `members`) and their allowed values so clients can branch on them predictably.
4. **Mastodon Docker composition** (when we get there): the exact Docker Compose topology — our server + Mastodon + optional relay, internal routable network, hostname assignments, and which Mastodon admin/REST endpoints to orchestrate (account creation, posting, following).
