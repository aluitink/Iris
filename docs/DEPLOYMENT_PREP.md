# Real-World Deployment Preparation (Phase 9)

> Phase 9 is **ideation + preparation only** — no live tests run here. The operator must provide a
> public, routable FQDN before any of this is exercised for real (that is Phase 13). This document is
> the **FQDN & TLS plan** (ROADMAP bullet 1) and the **instance bootstrap runbook** (bullet 2). It is
> grounded in the *real* config surface — `ActivityPubServerOptions` (`src/Iris.Server`), the `/ap/v1`
> route map, the NodeInfo handler, and the `SampleServer` seed — so the runbook steps are the actual
> commands/config an operator would use, not placeholders.
>
> Companion to [DEPLOYMENT.md](DEPLOYMENT.md) (the Phase 8 sample Docker composition — the reference
> topology and the smoke test we use to sanity-check a staging instance before it goes public).

## 1. FQDN & TLS plan

### 1.1 What the operator must provide

Iris binds to a base URI and builds every local IRI from it. Before a public instance can exist, the
operator must provide:

| Requirement | Detail | Why Iris needs it |
|---|---|---|
| **A public FQDN** | e.g. `iris.example.org` (A/AAAA record(s) pointing at the host). | The instance's base URI is `https://<FQDN>`; every actor/community IRI, WebFinger subject, and NodeInfo link derives from it. It must be **routable** (resolvable from the public internet) because *other* instances reach it by this name during federation. |
| **TLS (port 443)** | A certificate for the FQDN (Let's Encrypt or operator-managed). | ActivityPub federation is expected to be over HTTPS; the base URI will be `https://`. The reverse proxy terminates TLS and forwards to Iris on an internal HTTP port. |
| **A reverse proxy** | A TLS-terminating proxy in front of Iris (e.g. Caddy, nginx, or a cloud load balancer). | Iris itself (Kestrel) binds the internal HTTP port; the proxy handles the public `:443` → internal `:8080` mapping, TLS termination, and (optionally) rate limiting / WAF. |
| **The Iris base URI** | `https://<FQDN>` (no trailing slash, no port — the port is stripped by the proxy). | Set as `ActivityPubServerOptions.BaseUri`; Iris uses it to build absolute IRIs for local actors and in WebFinger. |

### 1.2 The exact Iris config surface

A production Iris host configures the instance through **`ActivityPubServerOptions`**
(`src/Iris.Server/ActivityPubServerOptions.cs`) — the single options object the server reads. The
fields relevant to a public deployment:

| Option | Type | Public-deployment value | Notes |
|---|---|---|---|
| `BaseUri` | `Iri?` | `new Iri("https://iris.example.org")` | **Required** for a public instance. Builds every local IRI and the WebFinger/NodeInfo links. Must be the **public** base (what other instances see), not the internal bind address. |
| `InstanceName` | `string?` | e.g. `iris-example-org` | Human-readable name; appears in NodeInfo (`metadata.name`) and as a default actor `name`. |
| `InstanceActorId` | `Iri?` | the instance's federation-identity actor IRI, e.g. `https://iris.example.org/ap/v1/u/iris` | The local actor the instance signs **outbound** federation as (inbound key-resolution fetches + delivery). Defaults to the first registered actor when unset (Resolved Decision #27). Set it explicitly so the instance's automation identity is deliberate. |
| `NamespaceIri` | `Iri?` | leave default (canonical Iris IRI) unless forking | The configurable `iris:` namespace base (Resolved Decision #9). A fork may override; a plain deployment keeps the default. |
| `CachePolicies` | `ServerCachePolicies?` | leave default unless tuning | Server-side cache TTL overrides (Resolved Decision #8). Defaults are fine for a first deployment. |
| `ProxySettings` | `ProxySettings?` | **tighten in production** | `AllowedHosts` (target allowlist for `POST /ap/v1/proxy/{target}`) + `MaxRequestsPerMinute` (default 60/actor/min). **Production default is permissive (empty allowlist = all targets)** — a public host should set `AllowedHosts` to the specific hosts it wants to proxy, or disable the proxy entirely. |

**Bind vs. advertise.** The two addresses are distinct and must not be confused:

- **Bind address** — the internal URL Kestrel listens on (e.g. `http://127.0.0.1:8080`). In the
  `SampleServer` host this is `Iris__HostName`/`Iris__Port` (`CreateWebHostBuilder` → `UseUrls`). In a
  production host it is the `ASPNETCORE_URLS` / Kestrel configuration. The reverse proxy points at this.
- **Advertised base URI** — `ActivityPubServerOptions.BaseUri` (e.g. `https://iris.example.org`). This
  is what goes on the wire in actor documents, WebFinger, and NodeInfo. Other instances dial **this**,
  which resolves (via the operator's DNS + TLS) to the public FQDN, which the proxy maps to the bind
  address.

> **Gotcha (from the Phase 8 sample):** if the bind host and the advertised base URI disagree, federation
> breaks. A remote instance fetches `https://iris.example.org/ap/v1/u/...` (the advertised IRI); if that
> does not round-trip to the instance that actually holds the actor, key resolution and delivery fail. The
> advertised base URI must be the **public** FQDN, and the operator's DNS/TLS/proxy must make that URI
> reachable. The Phase 8 Docker topology (each container's `Iris__HostName` = its service name, so the
> advertised base URI is routable on the internal network) is the in-container analogue of this rule.

### 1.3 Endpoints the public instance must expose

These are the routes `MapActivityPubEndpoints` maps under the `/ap/v1` prefix
(`src/Iris.Server/ActivityPubServerExtensions.cs`), plus the RFC well-known paths at the host root. All
must be reachable through the reverse proxy:

| Path | Method | Purpose | Public? |
|---|---|---|---|
| `/ap/v1/u/{handle}` | GET | Actor document. Public by default; the owner-only `privateKey` + `keyAlgorithm` extensions are served **only** to the authenticated owner (Basic auth) with `Cache-Control: no-store`. | yes (public doc); owner-only fields gated |
| `/ap/v1/c/{handle}` | GET | Community (`Group`) document. | yes |
| `/ap/v1/c/{handle}/members` | GET | Community members. | yes |
| `/ap/v1/c/{handle}/feed` | GET | Community feed (unified member outbox). | yes |
| `/ap/v1/c/{handle}/search` | GET | Community search. | yes |
| `/ap/v1/.well-known/webfinger` | GET | WebFinger under the versioned prefix. | yes |
| `/.well-known/webfinger` | GET | WebFinger at the RFC 8410 root path (the standard discovery path other instances use). | yes |
| `/ap/v1/nodeinfo/2.0` | GET | NodeInfo 2.0 document (software, protocols, metadata). | yes |
| `/.well-known/nodeinfo` | GET | NodeInfo discovery link (requires `BaseUri` to be set — it throws otherwise). | yes |
| `/ap/v1/u/{handle}/outbox` (and `followers`/`following`) | GET | Paged collections (page 1 = `OrderedCollection` carrying `first`; page N>1 = `OrderedCollectionPage`; `?page`, `?limit`, `?refresh=true`). | yes |
| `/ap/v1/u/{handle}/inbox` | POST | Inbound activity delivery (signature-validated; the `SignatureValidationMiddleware` validates POSTs). | yes (authenticated by signature) |
| `/ap/v1/proxy/{target}` | POST | Proxy fallback (Phase 6) — re-signs + relays an authenticated actor's request to an allowlisted target. | gated by `ProxySettings` |

The `Iris-Version` meta header is added to every mapped endpoint via a route-group filter.

## 2. Instance bootstrap runbook

Steps to stand up a public Iris instance against the operator-provided FQDN. The `SampleServer`
(`samples/SampleServer`) is the reference host: it shows the exact `Iris:` configuration surface, the
seed (actor + key + community), and the endpoint wiring. A production host replaces `InMemoryPersistenceProvider`
with a real `IPersistenceProvider` (Phase 14) and the Basic-auth validator with the chosen auth (Phase 14+),
but the config shape and bootstrap sequence are the same.

### Step 1 — Provision the FQDN + TLS (operator)

1. Point the FQDN (e.g. `iris.example.org`) at the host via an A/AAAA record.
2. Obtain a TLS certificate for the FQDN (Let's Encrypt or operator-managed).
3. Configure the reverse proxy to terminate TLS on `:443` and forward to Iris's internal bind address
   (e.g. `http://127.0.0.1:8080`). Ensure the proxy forwards the `Host` header (Iris derives the
   instance host from the base URI, not the request host, so a mismatched `Host` is tolerated, but a
   clean `Host` avoids surprises behind virtual-hosted proxies).

### Step 2 — Configure the Iris host

Set the `Iris:` configuration section (environment variables or app config) so the host binds the
internal address and advertises the public base URI. In the `SampleServer` host the surface is:

| Env var | Maps to | Public value |
|---|---|---|
| `Iris__HostName` | Kestrel bind host (internal) | `127.0.0.1` (or the internal interface) |
| `Iris__Port` | Kestrel bind port (internal) | `8080` |
| `Iris__Https` | bind scheme (internal) | `false` (the proxy provides TLS) |
| `Iris__Actor` | primary actor handle | e.g. `iris` |

Then set `ActivityPubServerOptions` (the authoritative source for the advertised base URI — the
`SampleServer` reads `Iris:HostName`/`Iris:Port` to build `BaseUri`, but a production host should set
`BaseUri` explicitly to the **public** FQDN so the advertised IRIs are correct regardless of the
internal bind address):

```csharp
services.Configure<ActivityPubServerOptions>(o =>
{
    o.BaseUri = new Iri("https://iris.example.org");        // public — what other instances dial
    o.InstanceName = "iris-example-org";
    o.InstanceActorId = new Iri("https://iris.example.org/ap/v1/u/iris"); // deliberate automation identity
    // o.NamespaceIri = ...   // leave default unless forking
    o.ProxySettings = new ProxySettings
    {
        AllowedHosts = ["relay.example.org"],   // tighten in production (default is permissive)
        MaxRequestsPerMinute = 60,
    };
});
```

### Step 3 — Generate keys + create the instance actor

The instance's federation-identity actor (the `InstanceActorId`) needs a key. The `SampleServer` seed
(`SeedSampleData`) is the reference implementation:

1. **Generate a key** — `KeyPairGenerator.GenerateEcP256(keyId)` (or `GenerateRsa`) where
   `keyId = {actorIri}#key-1`; store it in `IPersistenceProvider.Keys` (`PutKey`).
2. **Create the actor** — a `Person` with `Id = {actorIri}.Value`, `PreferredUsername`, `Name`; add a
   `publicKey` extension carrying the JWK (`kty: "EC"`, `crv: "P-256"`, `x`, `y`) — see
   `SeedSampleData` for the exact `ExtensionData["publicKey"]` shape.
3. **Store the actor** — `persistence.ActorStore.PutActorAsync(actor)`.

The actor's `publicKey` JWK is what a *remote* instance fetches to verify our signatures (Resolved
Decision #27: inbound key resolution reads the remote actor document's `publicKey` and reconstructs a
public-only `KeyPair`). A correct, reachable `publicKey` is the difference between "our federation
works" and "every inbound/outbound exchange fails validation."

### Step 4 — Create the community (optional but typical)

1. Create a `Group` actor (`Id = {baseUri}/ap/v1/c/{handle}`, `PreferredUsername`, `Name`).
2. Store it — `persistence.Communities.PutCommunityAsync(community)`.
3. Add members — `persistence.Communities.AddMemberAsync(communityIri, memberActorIri)`.
4. (The `SampleServer` seeds a post in each member's outbox so the community feed has content.)

### Step 5 — Verify discovery + federation readiness

Before declaring the instance public, confirm the read paths work end to end (this is what the Phase 8
smoke test checks for the sample stack; repeat it against the public FQDN):

```bash
# WebFinger (RFC 8410 root path) — the standard discovery path:
curl "https://iris.example.org/.well-known/webfinger?resource=acct:iris@iris.example.org"
# → { "subject": "acct:iris@iris.example.org",
#     "links": [ { "rel": "self", "type": "application/activity+json",
#                  "href": "https://iris.example.org/ap/v1/u/iris" } ] }

# NodeInfo discovery + document:
curl "https://iris.example.org/.well-known/nodeinfo"
curl "https://iris.example.org/ap/v1/nodeinfo/2.0"

# Public actor document (no privateKey):
curl "https://iris.example.org/ap/v1/u/iris"

# Paged outbox (page 1 = OrderedCollection with first):
curl "https://iris.example.org/ap/v1/u/iris/outbox"
```

All must return **200** with the instance's **public** base URI in the IRIs (not the internal bind
address). If the actor document's `id` shows `http://127.0.0.1:8080/...` instead of
`https://iris.example.org/...`, `BaseUri` is not set to the public FQDN — fix Step 2.

### Step 6 — Federation smoke (two instances)

Stand up a **second** instance (a second Docker compose stack per [DEPLOYMENT.md](DEPLOYMENT.md), or a
staging instance) and confirm cross-instance reachability: instance A resolves instance B's actor by
WebFinger over the network, and a signed follow/delivery round-trips. This is the Phase 8 topology
exercised against real (staging) FQDNs — it de-risks Phase 13 (live federation against third-party
instances) before any public instance is pointed at Mastodon/Lemmy/Threads.

## 3. What this phase does NOT do

- **No live tests.** No third-party instance is contacted; no public instance is stood up. Phase 13 is
  the live-federation phase and is **blocked on the operator-provided FQDN** from Step 1.
- **No real persistence / auth.** The runbook uses the `SampleServer`'s in-memory persistence + Basic
  auth as the reference; swapping in a real `IPersistenceProvider` and the production auth model is
  Phase 14+. The config shape and bootstrap sequence are unchanged by that swap.
- **The remaining Phase 9 bullets** (real-user enumeration design, compatibility matrix, test-harness
  extension, risk & gap register) are separate slices; they build on the FQDN/TLS plan and bootstrap
  runbook established here.
