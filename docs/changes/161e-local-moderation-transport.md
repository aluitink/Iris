# 161e — Local-moderation transport: mute/relay off the AP tree (19.0b.2b)

## Summary

Phase 19.0b.2b of the AP-native rework: the mute (F-07) and relay-subscription (F-06) **write** routes
move off the `/ap/v1` ActivityPub route tree onto a dedicated, non-AP `/local/v1` tree, so the `/ap/v1`
POST surface becomes outbox-only. Mute and relay subscription are Iris-specific **local** moderation
decisions — they are not ActivityStreams activities and are not interpreted from federation — so they
were never legitimate AP activities to begin with (the old `/ap/v1/u/{handle}/mutes/{target}` etc. routes
were a historical leak of local decisions onto the AP tree).

## What changed

### Server (`Iris.Server`)

- **New route tree.** `ActivityPubServerExtensions` now maps a separate
  `IEndpointRouteBuilder.MapGroup("/local/v1")` group (built from
  `Iris.Client.LocalModerationConstants.LocalRoutePrefix`) carrying the three local-moderation **write**
  routes:
  - `POST /local/v1/u/{handle}/mutes/{**target}` → `LocalMuteHandler` (`local-mute-endpoint`)
  - `POST /local/v1/u/{handle}/relays/{**target}` → `LocalRelayHandler` (`local-relay-endpoint`)
  - `POST /local/v1/c/{name}/mutes/{**target}` → `CommunityMuteHandler` (`community-mute-endpoint`)

  The group is **separate from the `/ap/v1` group**, so it does not carry the Iris AP version header (it
  is not an AP endpoint). The handlers are unchanged (still Basic-auth via `IActorCredentialValidator`,
  still non-AP, still record/remove the edge; `?unmute=true` / `?unsubscribe=true` remove).

- **Removed from `/ap/v1`.** The three `group.MapPost(...)` registrations (community mute, person mute,
  person relay) are gone from the `/ap/v1` group; a NOTE comment in their place records the relocation
  and that the **reads** (`GET /ap/v1/u/{handle}/mutes`, `/relays`, `/c/{name}/mutes`) remain on the AP
  tree (they are ordinary ActivityStreams collection reads).

- **`iris:capabilities` discovery (D4a).** Added `CapabilityMute = "mute"` and
  `CapabilityRelay = "relay"` to `ActivityPubServerConstants`. The person actor document
  (`BuildActorDocument`) now advertises `iris:capabilities = ["mute", "relay"]`, and the community document
  advertises `["feed", "members", "search", "mute"]` (a community can mute a member). A client discovers
  these specialized, non-AP capabilities from the actor/community document without guessing.

### Client (`Iris.Client`)

- **`LocalModerationConstants` (new).** A constants class owning the local tree: `LocalRoutePrefix =
  "/local/v1"` plus the route segments (`u`, `c`, `mutes`, `relays`). It lives in `Iris.Client` so the
  server (which depends on the client) references the same constant — correct dependency direction.

- **`LocalModerationClient` path derivation.** `LocalDecisionAsync` now builds the request URI via a new
  private `BuildLocalRequestUri(actorId, path, targetId, removeQueryString)`: it takes the actor IRI's
  host, the `/local/v1` tree, and the actor's identifying path segment (the substring from `/u/` or `/c/`
  onward), producing e.g. `https://host/local/v1/u/bob/mutes/{target}` from a `https://host/ap/v1/u/bob`
  actor IRI. The `/ap/v1` prefix is **not** reused — the write targets the non-AP tree.

## Decision (D5, recorded here)

**D5 — Local-moderation route tree = `/local/v1`, derived from the actor IRI.**
- Chose `/local/v1` (a new, explicitly non-AP prefix) over overloading `/ap/v1` with a sub-path or over
  keeping the writes on `/ap/v1`. Rationale: the AP route tree is the protocol surface; a mute/relay
  subscription is not an AP activity, so it does not belong there. A dedicated prefix makes the split
  unambiguous and lets the version-header AP filter apply only to genuine AP routes.
- The client derives the local URL from the actor IRI (host + the `/u/`/`/c/` segment) rather than being
  handed a separate base URI, because the local tree lives on the **same host** as the actor and reuses the
  actor's path segment — so no new config surface (`ActivityPubClientOptions`) is needed.

## Tests

- `Iris.Client.Tests/LocalModerationClientTests` — the 5 path assertions repointed from
  `{actorIri}/mutes/{target}` to the new `/local/v1` base (`LocalActorBase` const), and **2 new tests**
  added: `ActorIriWithApPrefix_TargetsLocalTree_NotApTree` (an `/ap/v1/u/bob` IRI maps to
  `/local/v1/u/bob/mutes/...`, never `/ap/v1`) and `CommunityIriWithApPrefix_TargetsLocalTreeCommunityMute`
  (an `/ap/v1/c/iris` IRI maps to `/local/v1/c/iris/mutes/...`).
- `Iris.Server.Tests/MutesCollectionIntegrationTests` + `RelaysCollectionIntegrationTests` — the raw
  `LocalPostAsync` helpers now target `/local/v1` (via a `ToLocalActorBase` helper mirroring the client's
  derivation); doc-comments updated.
- `Iris.Server.Tests/CommunityModerationIntegrationTests` — the raw community-mute POST now targets
  `/local/v1/c/{name}/mutes/...`.
- `Iris.Server.Tests/CommunitySearchIntegrationTests` — `CommunityDocument_AdvertisesCapabilities` now
  expects `["feed", "members", "search", "mute"]`.

## Result

Build clean (`TreatWarningsAsErrors` on); full suite green: **1,254 tests, 0 failed** (was 1,252; +2 new
client tests). The `/ap/v1` POST surface is now outbox-only (outbox publish + the shared inbox); the
specialized local capabilities (mute, relay) are on `/local/v1` and discovered via `iris:capabilities`.
