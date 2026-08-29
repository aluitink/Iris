# Iris — Architecture

> Part of the [Iris plan](../../PLAN.md). See also [Projects](PROJECTS.md), [Testing](TESTING.md), [Roadmap](../ROADMAP.md), [Coding Style](CODING_STYLE.md).

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing applications (Blazor clients, ASP.NET Core servers, or any .NET app).

## Design Principles

- **Clean and simple** — small, focused abstractions; no framework lock-in beyond .NET itself.
- **One client, two directions** — a single `net10.0` client library used both by client apps (browser/Blazor) and by servers (server-to-server).
- **Server as extension** — ActivityPub server capability is added to an existing ASP.NET Core app via `IServiceCollection`/`IApplicationBuilder` extensions.
- **Abstracted persistence** — the server stores data behind interfaces; the initial implementation is in-memory.
- **Proxied request fallback** — when a client hits CORS or 401/403 talking to a remote server, the request can be cascaded through *our* server (via `DelegatingHandler`) which signs and forwards it.
- **ActivityStreams via `KristofferStrube.ActivityStreams`** — we use the existing NuGet package for all ActivityStream/ActivityPub type definitions and JSON-LD serialization. `Iris.Core` does NOT re-implement the object model; it adds Iris-specific concerns (signing, identity, IRI helpers, validation) on top. See [Coding Style — 3rd-Party Types](CODING_STYLE.md#3rd-party-activitystreams-types) for the binding conventions.
- **Actor-keyed client auth** — the client authenticates to *our* server (Basic auth in v1), fetches the actor document which includes the private key, and signs subsequent client→server requests with that key. Server→server requests are signed by the actor's key (or system key for automated events).
- **Layered caching** — both client and server cache fetched objects (actors, collections, pages) with short TTLs to reduce chatter. Every cached read has a `bypassCache` / `forceRefresh` escape hatch.
- **Community-aware** — first-class support for `Group` actors as communities (Lemmy-style). Communities can follow other communities/actors, receive their content in a community inbox, and propagate it to local members. The client exposes unified feed/collection APIs that work identically for personal actors and communities.
- **Versioned API surface** — public API is versioned from day one via a **route prefix** (e.g. `/ap/v1/...`), an early first-class design decision. Library packages carry semantic versions; a version header (e.g. `Iris-Version`) is emitted as meta information. New major versions add a new prefix; existing prefixes stay stable and additive. A `?refresh=true`-style escape hatch keeps old clients working.
- **Configurable namespace** — the `iris:` namespace IRI is configurable per-deployment so forks can extend it with additional terms/capabilities. Collections advertise their extended features via an `iris:capabilities` property.
- **Integration-first testing** — tests are end-to-end against real in-process server instances (multiple hostnames), not a sprawl of small unit tests. Multiple in-memory server instances federate with each other to prove instance-to-instance compatibility. A downstream goal is a live Mastodon compatibility test orchestrated via Mastodon's admin API. See [Testing](TESTING.md).

## Solution Layout

```
Iris.sln
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions (on top of ActivityStreams)
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Server/                net10.0 — ASP.NET Core extensions, endpoints, middleware, community feeds
│   └── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
├── tests/
│   ├── Iris.Testing/               shared TestServer harness: ActivityPubHostFactory + TestSeeder/Jwk/JsonDoc
│   ├── Iris.Core.Tests/
│   ├── Iris.Client.Tests/
│   └── Iris.Server.Tests/
└── samples/
    ├── SampleServer/               minimal ASP.NET Core app hosting Iris.Server
    └── SampleBlazorClient/         Blazor WebAssembly app using Iris.Client
```

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

## Spec Research

Before and during implementation, **research the ActivityPub specification** (and related specs) to ensure we understand all requirements, not just the ones we've already inferred:

- **ActivityPub** (W3C): actor types, activity/object vocab, inbox/outbox semantics, signature requirements, delivery & retry, shared inbox, WebFinger, NodeInfo.
- **ActivityStreams 2.0**: object/activity model, collections & pagination, `@context` handling.
- **HTTP Signatures** (draft-cavage-http-signatures-03): signature base construction, header sets, key types.
- **WebFinger** (RFC 8615) & **JRD** (RFC 7033): discovery & response format.
- **NodeInfo 2.0**: server metadata.
- Cross-check against **Mastodon / Pleroma / Lemmy** implementations for real-world conventions (content types, pagination defaults, error handling, `Cache-Control`).

Findings that change our assumptions get folded back into this plan (and the [Resolved Decisions / Open Questions](ROADMAP.md#resolved-decisions) sections).
