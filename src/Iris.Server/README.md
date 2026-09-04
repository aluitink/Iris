# Iris.Server

The ActivityPub server library for Iris. Provides the HTTP endpoints, activity handlers,
persistence, and federation machinery for a self-hosted ActivityPub instance.

## Registration

```csharp
builder.Services.AddActivityPubServer(opts =>
{
    opts.BaseUri = new Iri("https://example.com");
    opts.InstanceName = "my-iris";
    // opts.NamespaceIri = new Iri("https://example.com/ns#"); // optional iris: namespace override
    // opts.SharedInboxIri = new Iri("https://example.com/ap/v1/shared-inbox"); // optional
});
builder.Services.AddInMemoryPersistence(); // or a file-backed provider

app.UseRouting();
app.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
```

## Routes

All routes are minimal-API (`Map*`) registrations in `MapActivityPubEndpoints`. There are no
attribute-based routes.

### ActivityPub tree — `/ap/v1/`

Carries the `Iris-Version: 1` response header on every response.

#### Actor (Person)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/ap/v1/u/{handle}` | Actor document. Public (cached, `?refresh=true` bypass). Basic-auth as owner returns the owner-only extension doc (`privateKey`, `keyAlgorithm`), `no-store`. |
| POST | `/ap/v1/u/{handle}/inbox` | Actor inbox — receives federation activities (Follow, Accept, Create, …). Requires HTTP signature. Per-peer rate-limited. |
| POST | `/ap/v1/u/{handle}/outbox` | Actor outbox publish — the write surface for activities the actor authors (Follow, Create, Like, Block, Flag, Undo, Accept, Reject, …). Requires signature from the acting actor. |
| GET | `/ap/v1/u/{handle}/inbox` | Actor inbox **read** (Decision 056): activities delivered TO the actor. Owner-only (Basic auth), `no-store`, paged `?page`/`?limit`. |
| GET | `/ap/v1/u/{handle}/{collection}` | Paged actor collections. `{collection}` ∈ `outbox`, `followers`, `following`, `liked`, `blocks`, `flags`, `mutes`, `relays`. Paged `?page`/`?limit`, cached (`?refresh=true` bypass). |
| GET | `/ap/v1/u/{handle}/feed` | Followed feed (F-14): the actor's home timeline — union of local + remote follows' outbox items. Paged. Not cached (merges remote outboxes over the wire). |

#### Community (Group)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/ap/v1/c/{name}` | Community document (Group actor). Addressed by handle. |
| GET | `/ap/v1/c/{name}/members` | Community members (actor IRIs). Paged, cached. |
| GET | `/ap/v1/c/{name}/feed` | Community unified feed (union of members' outbox activities). Paged, cached. |
| GET | `/ap/v1/c/{name}/outbox` | Community outbox read: activities the community authors (Follow + Undo). Paged, cached. |
| GET | `/ap/v1/c/{name}/search` | Community search: case-insensitive content search via `?q`. Paged `?limit`/`?offset`. |
| GET | `/ap/v1/c/{name}/{collection}` | Community collections. `{collection}` ∈ `following`, `followers`, `blocks`, `flags`, `mutes`. Paged. |
| POST | `/ap/v1/c/{name}/inbox` | Community inbox: receives federation activities (Follow, Create/Announce). Requires HTTP signature. |
| POST | `/ap/v1/c/{name}/outbox` | Community outbox publish: Follow / Undo of Follow. Requires signature from the community. |

#### Objects

| Method | Path | Description |
|--------|------|-------------|
| GET | `/ap/v1/{**path}` | Object document catch-all (F-02/F-03/F-10): serves a content object by IRI. `/replies` suffix serves the parent's replies (F-12). Deleted → Tombstone. Unknown → 404. |

#### Media

| Method | Path | Description |
|--------|------|-------------|
| GET | `/ap/v1/media/{id}` | Serves a stored attachment by its same-origin media IRI. Public, `max-age=31536000, immutable`. |
| GET | `/ap/v1/media/proxy?url=…` | Media proxy: fetches an external attachment URL once, stores it (deduped by URL + hash), serves same-origin. Fetch failure → 502. |

#### Search

| Method | Path | Description |
|--------|------|-------------|
| GET | `/ap/v1/search` | Global search / directory (F-13): searches local actors + stored content. `?q` (empty = list all). Paged `?limit`/`?offset`. |

#### Proxy

| Method | Path | Description |
|--------|------|-------------|
| POST | `/ap/v1/proxy/{**target}` | Proxy fallback: an authenticated actor's browser POSTs a cross-origin request to its own instance. Basic auth identifies the actor; target must pass `IProxyTargetPolicy` (allowlist + rate limit). Signs and forwards as the actor. |

#### OAuth2

| Method | Path | Description |
|--------|------|-------------|
| GET | `/ap/v1/oauth2/authorize` | OAuth2 authorization (RFC 6749 §4.1). `?client_id`, `?redirect_uri`, `?state`. Auto-approves (v1), 302-redirects with `?code=…&state=…`. |
| POST | `/ap/v1/oauth2/token` | OAuth2 token exchange: authorization code → Bearer token. |
| POST | `/ap/v1/oauth2/revoke` | OAuth2 token revocation (RFC 7009). Always 200. |

#### Instance metadata

| Method | Path | Description |
|--------|------|-------------|
| GET | `/.well-known/webfinger` | WebFinger (RFC 8410) at the host root. `?resource=acct:{handle}@{host}`. Resolves both person (`/ap/v1/u/{handle}`) and community (`/ap/v1/c/{name}`) handles. |
| GET | `/ap/v1/.well-known/webfinger` | WebFinger at the versioned prefix (symmetry). |
| GET | `/ap/v1/.well-known/nodeinfo` | NodeInfo discovery document (links to `/ap/v1/nodeinfo/2.0`). |
| GET | `/ap/v1/nodeinfo/2.0` | NodeInfo 2.0 (RFC 8555) instance metadata. |
| GET | `/ap/v1/health` | Health endpoint: runs all registered `IHealthCheck`s. 200 healthy/degraded, 503 unhealthy. No auth. |

### Local tree — `/local/v1/`

Non-federated, Basic-authenticated control surface. Does NOT carry the `Iris-Version` header.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/local/v1/u/{handle}/mutes/{**target}` | Local mute (person, F-07): hides a follow's content. `?unmute=true` removes. 204. |
| POST | `/local/v1/u/{handle}/relays/{**target}` | Local relay subscription (person, F-06): subscribes to a relay. `?unsubscribe=true` removes. 204. |
| POST | `/local/v1/u/{handle}/media` | Media upload: owner-only multipart POST. 201 + JSON `{iri, contentType, fileName}`. 10 MiB cap. |
| POST | `/local/v1/c/{name}/mutes/{**target}` | Local mute (community, 19.0b.2b): community-scoped mute. `?unmute=true` removes. 204. |

## JSON-LD extensions (`iris:` namespace)

The server advertises Iris-specific extensions under the `iris:` JSON-LD namespace (default
`https://iris.example/ns#`, configurable via `ActivityPubServerOptions.NamespaceIri`).
Strict ActivityStreams consumers ignore unknown terms.

### On the actor/community document

| Term | Value | When present |
|------|-------|--------------|
| `iris:capabilities` | `string[]` — e.g. `["feed","members","search","mute","relay","settings"]` | Always. Declares the specialized, non-core-AP capabilities available. |
| `iris:settings` | `string` (IRI) — the actor/community's outbox IRI | When the settings gate is active (`manuallyApprovesFollowers` for a person, `manuallyApprovesMembers` for a community). The IRI is where AP-native `Add`/`Remove` settings activities are published. |
| `feed` | `string` (IRI) | Person: the followed feed IRI (`/ap/v1/u/{handle}/feed`). Community: the community feed IRI (`/ap/v1/c/{name}/feed`). |
| `members` | `string` (IRI) | Community only: the members collection IRI. |
| `search` | `string` (IRI) | Community only: the community search IRI. |
| `blocks`, `flags`, `mutes` | `string` (IRI) | Person and community: moderation collection IRIs. |
| `relays` | `object` with `star` (IRI) | Person: the relays collection IRI. |
| `manuallyApprovesFollowers` | `boolean` (`true`) | Person: echoed when set (J-10 / Resolved Decision #46). |
| `manuallyApprovesMembers` | `boolean` (`true`) | Community: echoed when set (rides through ExtensionData). |
| `publicKey` | `object` `{id, owner, publicKeyPem, [JWK fields]}` | Both: the actor's signing key (JWK `kty`/`n`/`e` for RSA, PEM form). |

### Capability values

| Value | Meaning |
|-------|---------|
| `feed` | The actor/community has a followed feed (home timeline). |
| `members` | The community has a members collection. |
| `search` | The community (or instance) has a search endpoint. |
| `mute` | Local mute is available (person: F-07; community: 19.0b.2b). |
| `relay` | Local relay subscription is available (person: F-06). |
| `settings` | An AP-native settings surface exists (the gate can be toggled via `Add`/`Remove` of the actor's own document to the outbox). The `iris:settings` IRI is also present. |

### Reading extensions from the client

```csharp
using Iris.Client;
using Iris.Core;

// After fetching an actor/community document:
var actor = await client.GetActorAsync(actorIri);

// Read the settings IRI (null when no gate is set):
Iri? settingsIri = actor.GetSettingsIri();

// Read the capabilities list:
IReadOnlyList<string> caps = actor.GetCapabilities();

// Derive the settings IRI from the actor IRI:
Iri settings = actorIri.SettingsOf(); // appends "/settings"
```

### Other IRI helpers (`Iris.Core.Identity.IriExtensions`)

| Method | Appends | Example |
|--------|---------|---------|
| `InboxOf()` | `/inbox` | `…/u/alice/inbox` |
| `OutboxOf()` | `/outbox` | `…/u/alice/outbox` |
| `FollowersOf()` | `/followers` | `…/u/alice/followers` |
| `FollowingOf()` | `/following` | `…/u/alice/following` |
| `LikedOf()` | `/liked` | `…/u/alice/liked` |
| `BlocksOf()` | `/blocks` | `…/u/alice/blocks` |
| `FlagsOf()` | `/flags` | `…/u/alice/flags` |
| `MutesOf()` | `/mutes` | `…/u/alice/mutes` |
| `RelaysOf()` | `/relays` | `…/u/alice/relays` |
| `FeedOf()` | `/feed` | `…/c/devs/feed` |
| `RepliesOf()` | `/replies` | `…/notes/n1/replies` |
| `LikesOf()` | `/likes` | `…/notes/n1/likes` |
| `SharesOf()` | `/shares` | `…/notes/n1/shares` |
| `SearchOf()` | `/search` | `…/ap/v1/search` |
| `SettingsOf()` | `/settings` | `…/c/devs/settings` |

## Notes

- All writes are `POST` (no `PUT`/`DELETE`). Removal is signalled via query parameters
  (`?unmute=true`, `?unsubscribe=true`) or the `Undo` activity type.
- Paged collections use `?page` (1-based) / `?limit`. Page 1 returns an `OrderedCollection`
  with a `first` link; pages N>1 return `OrderedCollectionPage`.
- The `LocalCollectionPageCache` serves paged collection reads; `?refresh=true` bypasses.
- The actor document cache (`LocalActorDocumentCache`) serves `GET /ap/v1/u/{handle}`;
  `?refresh=true` bypasses.
- Media responses carry `max-age=31536000, immutable`.
