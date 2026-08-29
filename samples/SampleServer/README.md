# Iris SampleServer

A runnable Iris **ActivityPub server** that acts as the sample's "home" instance. It wires the
`Iris.Server` libraries (in-memory persistence, per-actor Basic auth, inbound federation signature
validation, and a rich seeded graph) into a single ASP.NET Core host, so both the full
`Iris.Client` pipeline (auth → sign → community feed → proxy fallback) and the inbound federation
path can be exercised against a real, running instance.

The single composition root is `SampleServer.CreateWebHostBuilder` — used by the runnable
`Program.Main` entry point and by the integration tests (which host it in an in-process
`TestServer`). Every seeded actor is served in-process, so the sample needs no external network to
demonstrate federation.

## Quick start

**Local (no Docker):**

```sh
dotnet run --project samples/SampleServer
# → Iris SampleServer running at http://localhost:5000
```

Override the `Iris:` configuration from the CLI (see [Configuration](#configuration)):

```sh
dotnet run --project samples/SampleServer --Iris:Port=8080 --Iris:Actor=alice
```

**Docker stack (two instances on `iris-net`):**

```sh
docker compose -f docker-compose.yml up --build -d   # iris-a → host:8081, iris-b → host:8082
./scripts/docker-smoke-test.sh                        # health + cross-container WebFinger + signed follow
docker compose -f docker-compose.yml down
```

**Logon credential:** every seeded actor authenticates with the shared sample password — the handle
is the username, `iris-sample` is the password:

| Actor | Username | Password |
|---|---|---|
| alice (primary) | `alice` | `iris-sample` |
| bob (local) | `bob` | `iris-sample` |
| carla (remote-host stand-in) | `carla` | `iris-sample` |

**Base URIs** (local run): actor `http://localhost:5000/ap/v1/u/alice`, community
`http://localhost:5000/ap/v1/c/iris`, WebFinger
`http://localhost:5000/.well-known/webfinger?resource=acct:alice@localhost`.

## Implemented features

Each feature maps to its endpoint(s), the library type(s) that implement it, and a pointer doc.
Routes are under the versioned `/ap/v1` prefix (Resolved Decision #10); the route prefix, content
types, and meta headers live in `Iris.Server.ActivityPubServerConstants`.

| Feature | Endpoints | Library | Pointer |
|---|---|---|---|
| Actor document + owner-only private key | `GET /ap/v1/u/{handle}` | `ActorDocumentHandler` | [ARCHITECTURE](../../docs/reference/ARCHITECTURE.md) |
| WebFinger (RFC 8410, root path) | `GET /.well-known/webfinger`, `GET /ap/v1/.well-known/webfinger` | `WebFingerHandler` | [Decision 031](../../docs/decisions/031-webfinger-root-path.md) |
| NodeInfo (RFC 8555) | `GET /ap/v1/nodeinfo/2.0`, `GET /ap/v1/.well-known/nodeinfo` | `NodeInfoHandler` | — |
| Paged collections (outbox / followers / following / liked / blocks / flags / mutes / relays) | `GET /ap/v1/u/{handle}/{collection}` | `CollectionEndpointHandler` + `LocalCollectionPageCache` | — |
| Followed feed (home timeline, pull-on-read) | `GET /ap/v1/u/{handle}/feed` | `FollowFeedHandler` + `IFollowFeedService` | [Decision 053](../../docs/decisions/053-followed-feed-pull-on-read.md) |
| Community doc / members / feed / search / following / followers | `GET /ap/v1/c/{name}[/members\|/feed\|/search\|/following\|/followers]` | `Community*Handler` | [Decision 036](../../docs/decisions/036-community-following-and-community-feed.md) |
| Community inbox (signed) | `POST /ap/v1/c/{name}/inbox` | `CommunityInboxHandler` | [Decision 036](../../docs/decisions/036-community-following-and-community-feed.md) |
| Replies (threading) | `GET /ap/v1/{objectPath}/replies` (dispatched by the object-document catch-all) | `ObjectDocumentHandler` + `IReplyStore` | [Change 054](../../docs/changes/054-f12-replies-threading.md) |
| Global search | `GET /ap/v1/search?q=…` | `GlobalSearchHandler` + `GlobalSearchService` | [Change 056](../../docs/changes/056-f13-global-search.md) |
| Inbox federation (signed; unsigned → 401) | `POST /ap/v1/u/{handle}/inbox` | `InboxHandler` + activity handlers + `SignatureValidationMiddleware` | [Decision 028](../../docs/decisions/028-two-sided-follow-lifecycle.md) |
| Object document + tombstone | `GET /ap/v1/{**path}` (catch-all) | `ObjectDocumentHandler` | [Decision 048](../../docs/decisions/048-content-object-write-path.md) |
| Proxy fallback (signed, as the acting actor) | `POST /ap/v1/proxy/{target}` | `ProxyHandler` | [Decision 037](../../docs/decisions/037-proxy-route-parameter-and-catch-all.md) |
| Local moderation (mute / relay) | `POST /ap/v1/u/{handle}/mutes/{target}` (toggle `?unmute=true`), `POST /ap/v1/u/{handle}/relays/{target}` (toggle `?unsubscribe=true`) | `LocalMuteHandler` / `LocalRelayHandler` + `IModerationStore` / `IRelayStore` | [Change 062 / 063](../../docs/changes/) |
| Capabilities discovery (`iris:capabilities`) | advertised on the actor + community documents | `ActivityPubServerConstants` | [Decision 010](../../docs/decisions/010-versioned-api-prefix.md) |

### Federation (inbound)

`UseSignatureValidation()` runs `SignatureValidationMiddleware` ahead of the endpoints. A signed
`POST` to a local inbox is verified end to end: the middleware buffers the body, resolves the
sender's public key from the sender's own actor document (via `IActorDocumentFetcher`), and checks
the HTTP signature (RSA, ECDSA, or Ed25519). Unsigned or invalidly-signed inbox POSTs are rejected
with **401**. The sample registers a `LocalActorDocumentFetcher` so local-host senders resolve
in-process; a sender on another host (carla) is treated as unknown — the honest federation boundary
for an instance with no knowledge of that host.

## Configuration

Read from the `Iris:` configuration section (environment variables `Iris__…` or `--Iris:…` CLI
flags; the WebHostBuilder resolves the bind URL early and `ConfigureServices` re-reads the
authoritative value):

| Key | Default | Sets |
|---|---|---|
| `Iris:HostName` | `localhost` | The bind host **and** the advertised IRI host (actor/community IRIs carry this host, so it must be routable by any peer that fetches them). |
| `Iris:Port` | `5000` | The bind port and the advertised IRI port. |
| `Iris:Https` | `false` | When `true`, the scheme is `https` (bind + advertised IRI); otherwise `http`. |
| `Iris:Actor` | `alice` | The primary actor's handle (its IRI is `{base}/ap/v1/u/{handle}` and it is the instance's `InstanceActorId`). |

The instance name is derived as `iris-{HostName}` and the namespace IRI is the fixed
`https://iris.example/ns#`.

## Seeded data

Seeding is synchronous and deterministic (no randomness in IRIs), so the graph is identical on every
start. IRIs follow the `{base}/ap/v1/u/{handle}`, `{base}/ap/v1/c/{name}`, and `{actorIri}#key-1`
conventions.

| Item | IRI / handle | Notes |
|---|---|---|
| alice | `{base}/ap/v1/u/alice` | Primary local actor; RSA key; the instance's `InstanceActorId`. |
| bob | `{base}/ap/v1/u/bob` | Second local actor; RSA key. |
| carla | `http://remote.example/ap/v1/u/carla` | Remote-host stand-in; **Ed25519** key (exercises the non-RSA load path). Not resolvable by this instance's inbound resolver — she behaves like a true remote actor. |
| The Iris Community | `{base}/ap/v1/c/iris` | `Group`; members alice + bob; follows carla; advertises `community:feeds` / `community:moderation` capabilities. |

Follow edges: alice ↔ bob (mutual), alice → carla, carla → alice.

Outbox content: one note per actor, a reply from bob to alice's note (`{bob}/notes/2`, `inReplyTo`
alice's note), and a like from carla of alice's note (recorded in `ILikeStore`). Together these give
the community feed, the followed-feed (home timeline), the replies thread, and the `liked`
collection real data to explore.

## How it is tested

- `tests/SampleServer.Tests` — in-process `TestServer` coverage of this exact composition root:
  Phase 7 pipeline tests (auth → sign → community feed → proxy fallback) plus the Phase 8 S1
  federation suite (`SampleServerFederationTests`: unsigned-inbox 401, signed RSA + Ed25519 follow
  accept, per-actor auth, remote-host unresolvability, and the seeded follow / community / reply /
  like edges). See [Change 070](../../docs/changes/070-sample-federation-ready.md).
- `scripts/docker-smoke-test.sh` — the Docker smoke path (health checks + cross-container WebFinger
  + a signed cross-container follow over genuine network I/O). Opt-in gate; see
  [DEPLOYMENT](../../docs/reference/DEPLOYMENT.md).
