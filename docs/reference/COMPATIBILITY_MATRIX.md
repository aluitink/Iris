# Compatibility Matrix (Phase 9)

> Phase 9 is **ideation + preparation only** — no live interop is run here. This document is the
> **compatibility matrix** (ROADMAP bullet 4): the target ecosystems and, for each, the concrete
> interop scenarios Phase 13 will verify (follow, post, receive, community/group, search, pagination,
> content types, signatures). It is grounded in the **real** Iris capability map (what the server
> sends/receives today, with file:line citations in §2), so every scenario states precisely what Iris
> is *expected* to do and where a gap will surface. Companion docs: [ENUMERATION_DESIGN.md](ENUMERATION_DESIGN.md)
> (how we find the targets) and [DEPLOYMENT_PREP.md](DEPLOYMENT_PREP.md) (how we stand up the host).

## 1. Purpose

Phase 13 (Live Federation Compatibility) runs real federation between our FQDN and real third-party
instances. This matrix is its **test plan**: it fixes (a) the platforms to test against, (b) the
scenarios each must cover, and (c) the **expected behavior** of Iris for each — derived from the
current implementation, not from an idealized spec. A scenario's outcome in Phase 13 is one of:

- **PASS** — Iris behaves as the matrix expects.
- **GAP** — Iris lacks the capability (the matrix already predicts this; recorded in the risk & gap
  register, fixed in a follow-up phase).
- **MISMATCH** — Iris behaves, but the platform does something the matrix didn't anticipate (a
  genuine live-test finding).

The matrix is the contract that turns "we tested against Mastodon" into a set of checkable
assertions.

## 2. Iris capability map (ground truth — what exists today)

Verified against the source. This is the basis for the "expected" column in §3–4.

**Outbound activities the server sends** (`src/Iris.Server`):
- `Accept` — the follow-response, built in `FollowIris.cs:44-49`; scheduled at `FollowActivityHandler.cs:119-122`
  for an *auto-accepted* inbound follow, and (Phase 19.0b, AP-native) delivered when the operator publishes an
  `Accept` to the followed actor's outbox (`OutboxPublishHandler`'s `Accept` branch).
- `Announce` — boost to local followers, `AnnounceIris.cs:50`, scheduled at `AnnounceActivityHandler.cs:127-130`.
- `Reject` — **now sent** (Phase 19.0b, AP-native): the operator publishes a `Reject` (built by
  `FollowIris.BuildReject`, `FollowIris.cs:58`) to the followed actor's outbox; `OutboxPublishHandler`'s
  `Reject` branch records the decision + removes the provisional edge and server-delivers the `Reject` to the
  follower. The server also *receives* `Reject` (`RejectActivityHandler.cs:41-69`). The legacy Basic-auth
  follow-decision endpoints (`/ap/v1/u/{handle}/follows/{followId}[/accept]`) that once were the only
  decision path were **removed** in Phase 19.0b — the outbox is the sole write path.
- **The server never sends `Create`/`Like`/`Undo`.** Posts are written directly to local outbox
  collections (`samples/SampleServer/Program.cs:243-244`), not delivered as signed `Create` activities to
  followers' inboxes. Delivery is async via `DeliveryWorker`, signed as the acting actor
  (`DeliveryWorker.cs:150-168`); failures are logged and dropped, not retried.

**Inbound activities the server handles** (registered at `ActivityPubServerExtensions.cs:203-207`):
- `Follow` (`FollowActivityHandler.cs:66`), `Accept` (`AcceptActivityHandler.cs:44`), `Reject`
  (`RejectActivityHandler.cs:41`), `Announce` (`AnnounceActivityHandler.cs:74`), plus a base-`Activity`
  catch-all for **community inboxes** (`CommunityInboxActivityHandler.cs:33,56`).
- **Unknown / unhandled inbound types are stored but silently not dispatched** — no log, no 501
  (`InboxProcessor.cs:49-53`). A `Create`/`Like` to a *person* inbox is stored, uninterpreted; to a
  *community* inbox it is recorded into local members' outboxes via the catch-all
  (`CommunityInboxActivityHandler.cs:87-89`). Handler exceptions propagate to the inbox endpoint as a
  500 (`InboxProcessor.cs:13-15`).

**Community / Group**:
- Remote actors **can follow local Groups** — the edge is recorded (`FollowActivityHandler.cs:96-104`) and
  answered with `Accept` (`FollowIris.cs` `BuildAccept`); follow ≠ membership grant
  (`FollowActivityHandler.cs:33-36`).
- Groups expose `members`, `feed` (union of local members' outboxes, `CommunityFeedService.cs:31-62`),
  `search`, `following`, `followers` (followers intentionally always empty,
  `ActivityPubServerExtensions.cs:1096-1102`).
- The server **never follows remote Groups** itself (no outbound group-follow path); the client's
  `GetCommunityFeedAsync` reads remote feeds read-only (`ActivityPubClient.cs:225-243`).

**Signatures**:
- Protocol: `draft-cavage-http-signatures-03` (`Signature` header, `Signatures.cs:24`). Two profiles
  sent: `ClientToServer` = `(request-target) host date`; `ServerToServer` = adds `digest content-type`
  (`SigningProfile.cs:13-25`, `Signatures.cs:65-72`).
- **Digest is SHA-256** (`sha-256=…`), not SHA-512 — Mastodon's `verify_body_digest!` only accepts
  `sha-256` (`Signatures.cs:59,106-110`).
- **The signature base is the `headers` lines joined with a newline separator and no trailing
  newline** (`Signatures.BuildSignatureBase`, `Signatures.cs:126-154`). This matches the de facto
  Fediverse convention (Mastodon's `signed_request.rb` uses `join("\n")`); draft-cavage-03's letter
  says each line is newline-terminated, but the live servers omit the final newline, so we do too.
- Validation accepts **either profile** (rebuilds the base from the `headers` present,
  `HttpSignatureVerifier.cs:57-73`). Algorithms: **RSA `rsa-sha256` and EC P-256 `ecdsa-p256-sha256`
  only — no EdDSA** (`Signatures.cs:79-85`, `KeyPairGenerator.cs:39,46`).
- **Inbound validation is POST-only** (GETs skipped to avoid key-resolution recursion,
  `SignatureValidationMiddleware.cs:41-57`); endpoints 401 unsigned POSTs
  (`ActivityPubServerExtensions.cs:584-590`). Remote public keys resolved from actor docs, JWK or PEM
  (`RemoteInboundKeyResolver.cs:79-108`).

**Content types / JSON-LD**:
- Produces `application/activity+json` (`ActivityJson.cs:23`); accepts both `application/activity+json`
  and `application/ld+json` on inbound (`ActivityJson.cs:26-28`). `@context` comes from the
  ActivityStreams library's attributes — Iris does **not** pin or extend the context; extended/inbound
  contexts pass through as unknown properties. No JSON-LD expansion; deserialization is via the
  polymorphic `IObjectOrLink` converter on `type`.

**Pagination**:
- Page 1 = `OrderedCollection` (self-`first`, `totalItems`); page N>1 = `OrderedCollectionPage`
  (`partOf`, `startIndex`, `totalItems`, `prev`, `next`) (`ActivityPubServerExtensions.cs:1260-1292`).
- Query shape: `?page` (1-based) + `?limit` (default 20, cap 100, `ActivityPubServerConstants.cs:115-120`);
  **no cursor support**. Applied to outbox, followers, following, members, community feed;
  `?refresh=true` bypasses the page cache.

**Search / discovery (server)**:
- Community search: `GET /ap/v1/c/{name}/search?q=&limit=&offset=` (`ActivityPubServerExtensions.cs:355-360`).
  **No global search, no directory endpoint.**
- WebFinger: `/.well-known/webfinger` (RFC 8410) and `/ap/v1/.well-known/webfinger`
  (`ActivityPubServerExtensions.cs:307-320`). NodeInfo 2.0: `/ap/v1/nodeinfo/2.0` +
  `/.well-known/nodeinfo` discovery link (`ActivityPubServerExtensions.cs:317-320`).

**Client**:
- `DeliverAsync` can deliver **any** `Activity` (only requires `activity is Activity`) — signed POST to
  any inbox IRI (`ActivityPubClient.cs:142-160`); raw via `SendAsync` (`:163-167`).
- The client does **not** validate inbound signatures (send-side `SigningHandler` only); validation is
  server-side middleware. Pipeline: `ProxyFallbackHandler → RetryHandler → JsonLdHandler → SigningHandler →
  transport` (`ActivityPubClientFactory.cs:47-90`); retry is GET/HEAD/OPTIONS-only, never POST
  (`RetryHandler.cs:146-149`).

## 3. Target ecosystems

The matrix covers the platforms a real Iris deployment is most likely to federate with, ordered by how
well they conform to the ActivityPub/ActivityStreams spec (best conformance = most likely PASS; each
also carries the live-test risk most worth resolving).

| Platform | Conformance | Notes for Iris interop |
|---|---|---|
| **Mastodon** | High (reference implementation) | Largest ecosystem; standard AS 2.0 + HTTP Sig 1.0 (draft-cavage-10). The primary "it should just work" target. Extended AS (Mastodon-specific properties like `sensitive`, `spoilerText`) pass through Iris's unknown-property path. |
| **Pleroma / Akkoma** | High | Lightweight, closely follows the spec; good secondary reference. Often exposes a public search/directory API (see enumeration design §3.5). |
| **Lemmy** | Medium | Community-centric; uses `Group` for communities with **Lemmy-specific semantics** (a Lemmy community is not a pure AS 2.0 `Group` — it has its own `t:`/`c:` IRIs and a different follow flow). The group-interop scenarios are the risk here. |
| **Threads** | Low / non-standard | Meta's AP implementation has a **non-standard AP surface** and no public directory/search. Hardest target; likely partial in Phase 13 (see risk & gap register). |
| **Iris (self)** | N/A (baseline) | Iris-vs-Iris federation is already covered by the in-process two-instance integration tests (Phase 4–7). Phase 13 uses a second FQDN or the Docker topology as the "other instance" sanity check before external platforms. |

> Coverage note: the matrix targets the four external ecosystems above. "And others" from the roadmap
> (e.g. Pixelfed, Misskey, GoToSocial) are structurally the same scenarios as Mastodon/Pleroma and are
> added in Phase 13 by extending the target list, not by new scenario definitions.

## 4. Interop scenarios (per platform)

Each scenario is one checkable assertion in Phase 13. The **Iris expected** column states what the
*current* implementation does (from §2); a scenario whose expectation is a known gap is marked
**[GAP]** — it is recorded in the risk & gap register and is the thing a follow-up phase fixes.
"Direction" is `out` (Iris → platform) or `in` (platform → Iris).

### 4.1 Follow (actor → actor)

| # | Scenario | Direction | Iris expected (ground truth) |
|---|---|---|---|
| F1 | Platform actor follows our local actor; we respond `Accept`; the platform sees the `Accept` in its inbox and the follow shows as accepted. | in→out | **PASS-expected.** `FollowActivityHandler` records the edge and schedules `Accept` (`FollowActivityHandler.cs:96-122`, `FollowIris.cs:44-49`). |
| F2 | We (via client `DeliverAsync`) follow a platform actor; the platform responds `Accept`; our `AcceptActivityHandler` records it. | out→in | **PASS-expected.** Client delivers signed `Follow`; inbound `Accept` handled (`AcceptActivityHandler.cs:44`). |
| F3 | Platform actor follows our local actor; we respond `Reject`. | out | **PASS-expected (Phase 19.0b, AP-native).** The operator publishes a `Reject` to the followed actor's outbox; `OutboxPublishHandler`'s `Reject` branch records the decision (removes the provisional edge) and server-delivers the `Reject` to the follower (`FollowIris.cs:58`). The legacy follow-decision endpoint is removed — the outbox is the sole write path. |
| F4 | We `Undo` a follow (un-follow); platform removes the relationship. | out | **[GAP]** No outbound `Undo` is constructed. Un-follow is not delivered. |

### 4.2 Post / receive (Create)

| # | Scenario | Direction | Iris expected (ground truth) |
|---|---|---|---|
| C1 | We post a Note; followers on the platform receive a signed `Create` in their inbox and can render it. | out | **[GAP]** The server writes posts to local outboxes but **never sends a `Create` activity** to followers' inboxes. A remote follower's inbox will not receive our posts. This is the single largest interop gap. |
| C2 | A platform actor posts a Note; it is delivered to a local actor's inbox. | in | **PASS-expected (stored).** The `Create` is received, signature-validated (POST), and **stored**, but there is no person-inbox `Create` handler, so it is not interpreted into a local feed for a person — only community inboxes surface it (see C4). |
| C3 | A platform actor posts a Note; it is delivered to a local **community** inbox and appears in the community feed / members' outboxes. | in | **PASS-expected.** The base-`Activity` catch-all records it into local members' outboxes (`CommunityInboxActivityHandler.cs:87-89`); the community `feed` surfaces it (`CommunityFeedService.cs:31-62`). |
| C4 | We receive a `Create` with an extended/unknown ActivityStreams type (e.g. a Mastodon `Video`/`Article`). | in | **PASS-expected (round-trips).** Unknown types deserialize via the `IObjectOrLink` converter and pass through as stored objects (no expansion). They will not be *interpreted*, but they must not be rejected. |

### 4.3 Community / Group

| # | Scenario | Direction | Iris expected (ground truth) |
|---|---|---|---|
| G1 | A platform user follows our local community (`Group`); we respond `Accept`; the user appears in the community `members`/followers. | in | **PASS-expected.** `FollowActivityHandler` records the edge for a `Group` and answers `Accept` (`FollowIris.cs` `BuildAccept`). |
| G2 | Our community follows a remote community/platform group. | out | **[GAP]** The server never follows remote Groups (no outbound group-follow path). |
| G3 | A platform community follows our local community (community→community). | in | **PASS-expected.** Same path as G1 (a `Group` following a `Group`); edge recorded, `Accept` sent. |
| G4 | A remote user posts into a community we follow; it lands in our local feed. | in | **PASS-expected** via the community-inbox catch-all (C3), *if* we were following it — but see G2 (we can't initiate the follow), so this is only reachable if the platform community follows us first. |

### 4.4 Search

| # | Scenario | Direction | Iris expected (ground truth) |
|---|---|---|---|
| S1 | A platform user searches for our community and finds it via the platform's global search/directory. | out | **[GAP]** The server exposes only per-community search (`/ap/v1/c/{name}/search`, `ActivityPubServerExtensions.cs:355-360`); there is **no global search or directory** endpoint for the platform to index us. |
| S2 | We search a platform instance (read-only reconnaissance). | out (client) | **Platform-dependent.** Iris has no search *client* method; this uses `SendAsync` against the platform's public search API (enumeration design §3.5). Not standard AP — recorded as a harness capability, not a federation assertion. |

### 4.5 Pagination

| # | Scenario | Direction | Iris expected (ground truth) |
|---|---|---|---|
| P1 | A platform client pages through our actor's outbox / followers / following using `?page` + `?limit`. | in (GET) | **PASS-expected.** `OrderedCollection` (page 1) → `OrderedCollectionPage` (N>1) with `prev`/`next`/`totalItems` (`ActivityPubServerExtensions.cs:1260-1292`); `?page` 1-based, `?limit` default 20 cap 100. |
| P2 | We page through a platform collection (read-only). | out (client) | **PASS-expected.** `GetCollectionAsync`/`GetCollectionItemsAsync` follow `next` to exhaustion (`ActivityPubClient.cs:170-233`); `CollectionQuery.Limit` bounds it. Note: Iris follows `next` links, so a platform that uses **cursor-based** pagination (some do) will stop at page 1 — a potential live-test mismatch. |

### 4.6 Content types / JSON-LD

| # | Scenario | Direction | Iris expected (ground truth) |
|---|---|---|---|
| T1 | We serve actor documents / collections as `application/activity+json`. | out (GET) | **PASS-expected.** All responses are `application/activity+json` (`ActivityJson.cs:23`). |
| T2 | A platform sends us an object with `application/ld+json` and an extended `@context`. | in | **PASS-expected (accepted).** Inbound accepts both `application/activity+json` and `application/ld+json` (`ActivityJson.cs:26-28`); extended contexts pass through as unknown properties (no expansion). |
| T3 | A platform sends a `Create` whose `object` uses a Mastodon-specific type (e.g. `toot`/`Video`). | in | **PASS-expected (round-trips, not interpreted).** Deserialized via `IObjectOrLink`; stored; not semantically processed for a person inbox (C2). |

### 4.7 Signatures

| # | Scenario | Direction | Iris expected (ground truth) |
|---|---|---|---|
 | SIG1 | A platform sends a signed POST (HTTP Sig, `draft-cavage-http-signatures-10`, RSA-SHA256); we validate and accept it. | in | **PASS-expected.** Validation accepts the cavage signature base from the `headers` list present (`HttpSignatureVerifier.cs:57-73`); RSA `rsa-sha256` supported (`Signatures.cs:79-85`). Note: Iris implements **draft-03**; a strict draft-10 sender is compatible because the base-string format is the same for the headers Iris checks (no trailing newline, `sha-256` digest) — live test confirms. |
| SIG2 | A platform sends a signed POST using **EdDSA** (Ed25519). | in | **[GAP]** Iris supports only RSA `rsa-sha256` and EC P-256 `ecdsa-p256-sha256` — **no EdDSA** (`KeyPairGenerator.cs:39,46`). An EdDSA-signed POST will fail validation → 401. |
| SIG3 | A platform sends an **unsigned** POST to our inbox. | in | **PASS-expected (rejected).** Endpoints 401 unsigned POSTs (`SignatureValidationMiddleware.cs:41-57`, `ActivityPubServerExtensions.cs:584-590`). |
 | SIG4 | We send a signed POST (ServerToServer profile, with `digest`); the platform validates it. | out | **PASS-expected.** ServerToServer profile adds `digest content-type`; the digest is `sha-256=…` and the base has no trailing newline (`Signatures.cs:59,65-72,126-154`) — both match what Mastodon reconstructs. A conformant platform validates it. |
| SIG5 | We fetch a remote actor doc / collection over **GET** (no signature). | out (GET) | **PASS-expected.** GETs are not signed by Iris and are not validated on receive (POST-only validation); remote public keys are resolved from actor docs when needed for POST validation (`RemoteInboundKeyResolver.cs:79-108`). |

## 5. Gap summary (predicted — feeds the risk & gap register)

Scenarios the **current** implementation will not satisfy, in priority order. These are the live-test
findings Phase 13 will confirm and the follow-up phases will fix:

1. **No outbound `Create` (C1)** — the largest gap. Remote followers never receive our posts as signed
   `Create` activities. This blocks "post and have it federate," the core use case.
2. **Outbound `Reject` (F3) / `Undo` (F4) — now sent (Phase 19.0b, AP-native).** The operator publishes a
   `Reject` (or an `Undo` of a follow) to the followed actor's outbox; `OutboxPublishHandler` records the
   decision and server-delivers it to the follower. The legacy follow-decision endpoint is removed — the
   outbox is the sole write path. (Re-check against live Mastodon in 19.1.2; a platform that ignores a
   `Reject` is a MISMATCH, not an Iris gap.)
3. **No outbound group-follow (G2)** — Iris communities cannot initiate following a remote community.
4. **No global search / directory (S1)** — platforms cannot discover Iris communities via a global index.
5. **No EdDSA validation (SIG2)** — Ed25519-signed inbound posts are rejected; platforms using EdDSA
   (some Pleroma/Akkoma configs) will fail to deliver to us.
6. **Person-inbox `Create`/`Like` uninterpreted (C2)** — posts to a local *person* inbox are stored but
   not surfaced in a personal feed (only community inboxes interpret them).

These six are the concrete, predicted gaps. Phase 13's job is to (a) confirm the PASS-expected
scenarios actually pass against real instances, and (b) confirm these six gaps surface as predicted —
then the gap register (next Phase 9 slice) turns them into tracked follow-up work.

## 6. What this phase does NOT do

- **No live interop.** No third-party instance is contacted; no scenario is run. Phase 13 executes
  §4 against real hosts, gated by the FQDN + env flag (the test-harness-extension slice).
- **No code changes.** The gaps in §5 are *predicted from the source*, not fixed here. `dotnet build` /
  `dotnet test` are unchanged (444/444).
- **The remaining Phase 9 bullets** (test-harness extension, risk & gap register) are separate slices.
  The test-harness slice defines *how* §4 is run; the risk & gap register promotes §5's six gaps (plus
  the Threads/Lemmy unknowns from the enumeration design) into tracked items.
