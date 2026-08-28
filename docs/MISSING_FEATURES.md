# Phase 12 — Spec Conformance & Missing-Feature Inventory

> Part of the [Iris plan](../PLAN.md). Companion to [ARCHITECTURE.md](ARCHITECTURE.md) (§Spec Research), [RISK_GAP_REGISTER.md](RISK_GAP_REGISTER.md) (the G-1…G-6 capability register), and [PHASE_11_USER_JOURNEYS.md](PHASE_11_USER_JOURNEYS.md) (the J-1…J-22 usability register).
>
> This is the Phase 12 output for the **"Missing-feature inventory"** and **"Spec conformance audit"** bullets: a single, severity-ranked list of (a) what the implementation **does** today, (b) what the ActivityPub / ActivityStreams 2.0 / HTTP-Signatures / WebFinger / NodeInfo specs **require or widely expect** that is **missing**, and (c) a **prioritized fix plan** (spec-mandated vs nice-to-have, interop impact, effort) that drives "Implement high-priority gaps" and the "Conformance test suite".
>
> **No production code changed** in producing this — it is a research/audit slice (the Phase 12 "Missing-feature inventory" + "Spec conformance audit" bullets). The fix-plan items are the input to the next slices.

## How to read this

- **ID** — `F-#` (a missing feature / spec-mandated gap) or `C-#` (a spec conformance note / deviation). `F-` items are *absent* behavior; `C-` items are *present but non-conformant* behavior.
- **Spec** — the requirement (W3C ActivityPub, ActivityStreams 2.0, draft-cavage-03 HTTP Signatures, RFC 8615 WebFinger, NodeInfo 2.0) and the concrete clause.
- **Severity** — **Blocker** (breaks a core use case), **High** (breaks a common interop path or a spec-mandated requirement), **Medium** (degrades a common path or a widely-expected feature), **Low** (edge case / nice-to-have).
- **Interop impact** — what *actually* breaks against real servers (Mastodon / Pleroma / Lemmy / Rayven), with the Phase 13 target that would confirm it.
- **Cross-ref** — the J-register (user-journeys) / G-register (risk-gap) entry, if any, that already tracks the same gap.
- **Effort** — **S** (a focused slice, a day or less), **M** (a multi-file slice, a few days), **L** (a multi-slice effort).

The two severity buckets that matter for prioritization: **spec-mandated** (an ActivityPub peer *requires* it to interop) vs **nice-to-have** (widely-expected but not spec-mandated). A "High" that is spec-mandated ranks above a "High" that is nice-to-have.

## 1. What is implemented today (the conformance baseline)

This is the "does it" column — the surface the audit checks against. Anything not listed here is, by construction, missing or partial.

### Server (`Iris.Server`) — endpoints

| Surface | Route(s) | Notes |
|---|---|---|
| Actor document | `GET /ap/v1/u/{handle}` | Public (owner-only `privateKey` for Basic auth); RSA-2048 `publicKeyPem` (Slice 11.8); echoes `manuallyApprovesFollowers` when true (Slice 11.10). |
| WebFinger | `GET /ap/v1/.well-known/webfinger` **and** `GET /.well-known/webfinger` | Served at **both** the route-prefixed and the bare `/.well-known` path (RFC 8615 requires the bare path; the prefixed copy is an Iris convenience). J-22. |
| NodeInfo | `GET /ap/v1/nodeinfo/2.0` + `GET /ap/v1/.well-known/nodeinfo` | Instance metadata (software, openRegistrations, usage). |
| Inbox | `POST /ap/v1/u/{handle}/inbox` | Signature-gated (401 on unsigned / invalid). |
| Paged collections | `GET /ap/v1/u/{handle}/{outbox\|followers\|following}` | `OrderedCollection` (page 1, `first`) / `OrderedCollectionPage` (page N>1), `?page=N` / `?limit=N`, `next` on page 1 via `ExtensionData` (Slice 11.1). |
| Community document | `GET /ap/v1/c/{name}` | The community `Group` actor doc. |
| Community members | `GET /ap/v1/c/{name}/members` | Paged. |
| Community feed | `GET /ap/v1/c/{name}/feed` | Union of local members' outboxes, newest first. |
| Community search | `GET /ap/v1/c/{name}/search` | `?q`, `?limit` / `?offset` (Decision #6). |
| Community collections | `GET /ap/v1/c/{name}/{following\|followers}` | `following` = community's follows set; `followers` = empty (the documented asymmetry, J-12). |
| Community inbox | `POST /ap/v1/c/{name}/inbox` | Signature-gated. |
| Proxy fallback | `POST /ap/v1/proxy/{target}` | Basic auth + `IProxyTargetPolicy` (allowlist + rate limit). |

### Server — inbound activity handlers (the inbox dispatch)

Registered in `ActivityPubServerExtensions` (`AddSingleton<IActivityHandler, …>`); the `InboxProcessor` prefers the **most specific** handler.

| Handler | Activity | Behavior |
|---|---|---|
| `FollowActivityHandler` | `Follow` | Records the follow edge (person → `IFollowStore`; community → `ICommunityStore`); schedules `Accept` **unless** `manuallyApprovesFollowers` (Slice 11.10). |
| `AcceptActivityHandler` | `Accept` | Finalizes a follow the local actor made. |
| `RejectActivityHandler` | `Reject` | Removes a follow edge (the inverse of `Accept`; the operator path for `manuallyApprovesFollowers`, Slice 11.10). |
| `CreateActivityHandler` | `Create` | Records in the recipient's outbox (person's own / community members'); **federates to the author's remote followers** signed as the author (Slices 11.6 + 11.7). |
| `AnnounceActivityHandler` | `Announce` | Propagates a boost to **local** followers. |
| `UndoActivityHandler` | `Undo` | Removes the follow edge of the undone `Follow` (Slice 11.9). |
| `CommunityInboxActivityHandler` | `Activity` (catch-all) | Records `Like` / other content activities for a local community. |

### Client (`Iris.Client`) — `IActivityPubClient`

`GetObjectAsync`, `GetActorAsync`, `DeliverAsync` (returns a bare `int` status, J-7), `FollowAsync` (Slice 11.4), `PostNoteAsync` (Slice 11.5), `SendAsync` (escape hatch), plus collection enumeration (`GetCollectionItemsAsync` / `GetCollectionAsync`).

### Cross-cutting

- **Signing** (draft-cavage-03): `ClientToServer` profile = `(request-target) host date`; `ServerToServer` profile = `(request-target) host date digest content-type`. RSA-2048 (`RSA-SHA256`) default + EC P-256 (`ECDSA-P256-SHA256`) (Slice 11.8). **No EdDSA.**
- **Delivery** (`DeliveryService` / `DeliveryWorker` / `InMemoryDeliveryQueue`): per-actor signing, in-memory queue; retry/dead-letter is a documented extension point (the worker's XML doc states "a production host may layer retry/dead-letter on top").
- **WebFinger** (RFC 8615): `WebFingerClient` / `WebFingerDiscoveryService` / `WebFingerCache` — handle→IRI (`acct:` subject).
- **NodeInfo 2.0**: served by the server; the client has **no** NodeInfo consumer (it is discovery-side only, exercised by the Phase 9 enumeration design).
- **Key model**: PEM private (PKCS#8) + PEM public (PKIX, plus PKCS#1 RSA public import, Slice 11.8); in-memory `IKeyStore` for the session.

## 2. Missing-feature inventory (the "does not" column)

Severity-ranked. **Spec-mandated** items are marked **★** — an ActivityPub peer requires them to interop.

### Tier 0 — Blockers (break a core use case)

| ID | Gap | Spec | Severity | Interop impact | Cross-ref | Effort |
|---|---|---|---|---|---|---|
| ~~**F-01** ★~~ ✅ | ~~**`sharedInbox`** is neither **served** on the actor document's `endpoints` nor **used** as a delivery target.~~ **Resolved (Slice 12.2):** an instance with `ActivityPubServerOptions.SharedInboxIri` set advertises `endpoints.sharedInbox` on its public actor/community documents (serve side), and `DeliveryService.DeliverToActorAsync` resolves a remote recipient's delivery target to its advertised `endpoints.sharedInbox` from its document (via `IActorDocumentFetcher`, cached through the `RemoteActorCache`), falling back to the per-actor inbox when the document is absent or advertises no `sharedInbox` (consume side). | AP §5.1.3 ("An actor MAY include a `sharedInbox` … to reduce the load on the server") | ~~**Blocker**~~ | (was) Mastodon, Pleroma, and most production servers publish `sharedInbox` and prefer it; a remote instance delivering a `Create` to an absent shared inbox would 404, and Iris's per-follower fan-out (Slice 11.7) was the high-cost path. Both halves now work. Phase 13 (Mastodon) is unblocked on this gap. | J-18 (outbound delivery) | **M** |

### Tier 1 — High (break a common interop path or a spec-mandated requirement)

| ID | Gap | Spec | Severity | Interop impact | Cross-ref | Effort |
|---|---|---|---|---|---|---|
| **F-02** ★ | **No `Update` handler.** An inbound `Update` (an actor editing/deleting their own profile or a post) is uninterpreted — the stored object is never refreshed. | AP §5.2.1.7 (`Update`: "…the object being updated"); AS2.0 `Update` | **High** | A remote user editing their profile (or a post) does not propagate — Iris serves stale data forever. Phase 13 (follow → receive posts) will surface stale profiles. | — | **S** |
| **F-03** ★ | **No `Delete` handler.** An inbound `Delete` (an actor removing a post) is uninterpreted — the tombstoned object remains in outboxes/feeds. | AP §5.2.1.5 (`Delete`: "…the object being deleted is … a `Tombstone`"); AS2.0 `Tombstone` | **High** | A remote user deleting a post leaves it live on Iris. Phase 13 (follow → receive → delete) will show ghost posts. | — | **S** |
| **F-04** | **No `Like` handler (no `liked` collection).** Inbound `Like` is recorded only via the community catch-all for a local community; a person's `Liked` collection and `Like` interpretation (counts, notification) are absent. The `Actor.Liked` collection endpoint is not served. | AP §5.2.1.2 (`Like`); AS2.0 `liked` relationship | **High** | Likes from/for a local person are not surfaced or counted. A "nice-to-have" in some clients, but Mastodon surfaces a `liked` collection. Phase 13. | — | **M** |
| **F-05** ★ | **EdDSA (Ed25519) signing/verification is absent.** Only RSA + EC P-256 are generated/verified. An inbound post signed with Ed25519 → 401. | AP §5.1.4 (HTTP Signatures, which permits `ed25519`); draft-cavage-03 | **High** | **Pleroma and some modern servers sign with Ed25519 by default.** Iris rejects their federation traffic outright. Phase 13 (Pleroma) will be a hard block. | J-4 / G-5 | **M** |
| **F-06** | **No `relay` / `star` capability.** A relay (a `star`-subscribed fan-out server) cannot be configured; Iris neither subscribes to a relay nor serves as one. | AP §5.1.3 (relays); `relay` actor property | **High** | Small instances rely on relays for reach. Without it, a follower's content is only delivered 1-to-1 (the `sharedInbox` gap F-01 compounds this). Phase 13. | — | **L** |
| **F-07** | **No `Block` / `Mute` / `Flag` moderation.** A `Block` (or `Add`/`Remove` of a `block` tag) from a local or remote actor is uninterpreted; there is no moderation surface (list pending follows, block, mute, flag a post). | AP §5.2.1.3 (`Block`); AS2.0 `Flag` / `Invite` / `Offer` | **High** | An operator cannot block a spammer; a remote `Block` does not stop that actor's content. Directly tied to the `manuallyApprovesFollowers` moderation surface (Slice 11.10) which is the *gate* but has no *UI*. | J-10 (moderation UI, Phase 12) | **L** |
| **F-08** | **No `Move` handler.** An actor moving to a new IRI (with a `Move` + key) is uninterpreted — Iris keeps following the old IRI and fails to re-resolve. | AP §5.2.1.6 (`Move`) | **High** | When a user migrates servers, Iris does not follow them; the follow edge points at a dead actor. Phase 13. | — | **S** |
| **F-09** | **No `Add` / `Remove` handlers.** These are the spec's primitive for modifying a collection (e.g. adding a follower to a `followers` collection, or a community's `members`). Iris records follow edges in its own stores but does not interpret `Add`/`Remove`. | AS2.0 `Add` / `Remove`; AP §5.2.1.1 / 5.2.1.4 | **Medium** | A remote server that represents a community's membership via `Add`/`Remove` (rather than a `Follow`) will not update Iris's membership. Most servers use `Follow` for this, so impact is lower than the others. | J-12 (community `followers` asymmetry) | **S** |
| **F-10** | **No `Tombstone` serving.** When an object is deleted (F-03) or a collection item is removed, Iris does not serve the AS2.0 `Tombstone` (a `type: ["Tombstone"]` object with the original `id`). | AS2.0 `Tombstone` | **Medium** | A client fetching a deleted object gets a 404 instead of a `Tombstone` (the spec's "deleted" marker). Low user-visible impact; mostly a conformance detail. | (with F-03) | **S** |
| **F-11** | **No `Object`-type handling for `Article` / `Event` / `Place` / `Video` / `Image` / `Audio`** beyond generic storage. Iris stores whatever object arrives in `Create`/`Announce`, but does not interpret `Article`-specific fields (`publishedTime`, `duration`, `inLanguage`), nor render attachments (`attachment` / `tag`). | AS2.0 object model | **Medium** | A remote `Article` (long-form) is stored but its rich fields are not surfaced; an image/video `attachment` is not rendered in a feed. Phase 13 (Lemmy posts are `Note`s; Mastodon toots are `Note`s with `attachment`) — attachments matter for a real feed. | — | **M** |
| **F-12** | **No `tag` / `attachment` / `inReplyTo` thread rendering.** `Note` threads (`inReplyTo`), mentions (`tag`/`Mention`), and `attachment` are not interpreted — a reply is stored but not threaded, a mention is not linked, an attachment is not shown. | AS2.0 `inReplyTo`, `tag`, `attachment` | **Medium** | A feed that cannot thread replies or show mentions/attachments is unusable for real conversation. Phase 13 (Mastodon threads, Lemmy comments). | — | **M** |
| **F-13** | **No global search / directory.** Only per-community search (`/c/{name}/search`) exists. There is no instance-wide actor search, post search, or directory. | AP (no mandate) — widely-expected (Mastodon `/#/directory`, `/api/v2/search`) | **Medium** | A user cannot discover actors or content outside a community they already follow. | J-15 / G-4 | **M** |
| **F-14** | **No "followed feed" endpoint** — a remote actor's posts (followed) are not aggregated locally into a feed the user can read. The `following` collection is served, but the *content* of followed actors is not pulled/merged. | AP (no mandate) — the core "my timeline" use case | **Medium** | The single most-used feature of any federated client (the home timeline) is absent. A user follows someone and cannot read what they post. | J-19 | **L** |
| **F-15** | **No `liked` / `following` *content* federation to followers beyond `Create`.** `Announce` is federated to local followers (boost), but a local `Announce` is not federated *outbound* to the author's remote followers (mirror of `Create` federation, Slice 11.7). | AP (no mandate) — boosts | **Low** | A local boost does not reach the author's remote followers. Minor; boosts are less central than posts. | (mirror of Slice 11.7) | **S** |

### Tier 2 — Medium (degrade a common path or a widely-expected feature)

| ID | Gap | Spec | Severity | Interop impact | Cross-ref | Effort |
|---|---|---|---|---|---|---|
| **F-16** | **No `Offer` / `Invite` / `Join` / `Leave` handlers** (community membership via the spec's membership primitives, as opposed to `Follow`). | AS2.0 | **Medium** | A community that manages membership via `Invite`/`Join` (some servers) does not sync with Iris. Most use `Follow`. | — | **S** |
| **F-17** | **No `Read` / `View` / `Listen` / `Travel` / `Arrive` handlers** (the intransitive-activity family). | AS2.0 intransitive activities | **Low** | These are rarely used in federation; absent handling is acceptable for v1. | — | **S** |
| **F-18** | **No `unordered` `Collection` support** (only `OrderedCollection` / `OrderedCollectionPage`). | AS2.0 `Collection` | **Low** | Rarely used; `OrderedCollection` covers the realistic case. | — | **S** |
| **F-19** | **`DeliverAsync` returns a bare `int`** — no typed result (status + body), no typed exception on non-2xx. | (not a spec item — API ergonomics) | **Medium** | A caller cannot distinguish 401/404/429 from success without checking the int. Friction for any integrating user. | J-7 | **S** |
| **F-20** | **No OAuth2 bearer path** — only Basic auth (`privateKey` PEM) for actor identity. The `IClientAuthenticator` seam exists but has only the Basic implementation. | AP §5.1.5 (OAuth is the recommended mechanism; Basic is permitted for testing) | **Medium** | Real clients need OAuth2 (the `provideClientKey` / `oauthAuthorizationEndpoint` / `oauthTokenEndpoint` `Endpoints` are not served). The in-memory key (J-1) compounds this — a WASM host re-authenticates on every refresh. | J-1 / J-2 | **L** |
| **F-21** | **No key-rotation invalidation path** — a rotated remote key is served stale until the `RemoteKeyCache` TTL (1h) or `?refresh=true`; there is no active invalidation (e.g. on a 401, re-fetch and retry). | (not a spec item — operational) | **Medium** | After a key rotation, inbound posts 401 for up to an hour. A runbook + a rotation test are absent. | J-3 / O-3 | **S** |
| **F-22** | **No end-to-end delivery-retry / dead-letter.** The queue is in-memory with no persistent retry/backoff; a transient remote failure drops the delivery (the worker's doc defers retry to a "production host"). | AP §5.1.3 (reliability); operational | **Medium** | A transient 5xx on the remote side loses a federation delivery. Real deployment needs at-least-once delivery. | J-20 (delivery failures logged, not surfaced) | **M** |
| **F-23** | **`FeedFilter?` is accepted but ignored.** The community feed/search accept a filter parameter that has no effect. | (not a spec item) | **Low** | Misleading API surface — a caller's filter is silently dropped. | J-14 | **S** |
| **F-24** | **A community's `followers` collection is always empty** (the follows/followers inversion is undocumented in the wire contract). A community *follows* actors (its `following`), but is not *followed by* them in a recorded `followers` set. | AS2.0 `followers` relationship | **Medium** | A remote server checking a community's `followers` collection sees it empty — a conformance surprise. The route comment + `TESTING.md` should document the inversion. | J-12 | **S** |
| **F-25** | **No `Move`-driven actor re-resolution** (the read side of F-08) — and no **`key` rotation on the actor document** (a `publicKey` with a `replaces`/`replacedBy` is not honored). | AP §5.1.2 (actor `publicKey`); AS2.0 `Move` | **Medium** | Compounds F-08 / F-21 — even a correctly-handled `Move` would need Iris to swap keys. | (with F-08, F-21) | **M** |

### Tier 3 — Low (edge cases / nice-to-have)

| ID | Gap | Spec | Severity | Interop impact | Cross-ref | Effort |
|---|---|---|---|---|---|---|
| **F-26** | **No `Question` / `poll` support** (a Mastodon poll is a `Question` with `option`/`ended`/`votes`). | AS2.0 `Question` | **Low** | Mastodon polls render as plain text. Phase 13 (Mastodon) — nice-to-have. | — | **M** |
| **F-27** | **No `Emoji` / custom-emoji (`Mention`-like `tag`) support.** | Mastodon extension | **Low** | Custom emoji render as raw `tag` strings. | — | **S** |
| **F-28** | **No `summary` / `sensitive` (NSFW) handling.** A `Note`'s `summary` (spoiler) and `sensitive` flag are not surfaced. | Mastodon extension (AS2.0 `summary`) | **Low** | NSFW content is not blurred/flagged. | — | **S** |
| **F-29** | **No `url` (canonical URL) handling for objects** — a post's canonical `url` (the HTML page) is not surfaced for a "view in browser" link. | AS2.0 `url` | **Low** | Minor UX. | — | **S** |
| **F-30** | **WebFinger served at two paths** (the route-prefixed `/ap/v1/.well-known/webfinger` **and** the bare `/.well-known/webfinger`). The bare path is the RFC-required one; the prefixed copy is an Iris convenience. | RFC 8615 | **Low** | No interop impact (the bare path is present and correct); a discoverability nit. | J-22 | **S** (doc) |
| **F-31** | **No `application/ld+json` *production*** — Iris produces `application/activity+json` and *accepts* both (Decision #4). | AP (both are valid) | **Low** | Some servers prefer to receive `ld+json`; producing `activity+json` is spec-valid and widely accepted. | (Decision #4) | **S** |

## 3. Spec conformance notes (the "present but non-conformant" column)

These are behaviors that exist today but deviate from the spec in a way worth recording (some are deliberate, some are gaps in disguise).

| ID | Note | Spec | Severity | Status / disposition |
|---|---|---|---|---|
| **C-01** | **WebFinger is served at both `/ap/v1/.well-known/webfinger` and `/.well-known/webfinger`.** The bare path is the RFC 8615 requirement; the prefixed path is an Iris route-prefix convenience (Decision #10). | RFC 8615 §4.1 | **Low** | **Deliberate** — the bare path satisfies the spec; the prefixed copy is additive. Documented in the route comment. (F-30.) |
| **C-02** | **GETs are not signature-validated** — only inbox POSTs are gated. A key-resolution GET (fetching an actor doc to verify a signature) would recurse, so GETs are open by design. | AP §5.1.3 (signing is for *delivery*; document fetching is unsigned) | **Low** | **Deliberate** — matches real-world ActivityPub (actor docs are public GETs). A one-line doc note on `SignatureValidationMiddleware` improves discoverability (J-5). |
| **C-03** | **The `ServerToServer` signature profile signs `content-type` but the inbound validator does not require it.** The outbound signer covers `content-type` (per draft-cavage-03 for a body-carrying request); the inbound verifier is lenient (does not mandate the full header set). | draft-cavage-03 | **Medium** | **Lenient by design** (Decision #4, content-type flexibility) — accepting a superset is safe; a strict peer's signature is still verified. Worth a conformance-test assertion that the *outbound* signature base includes `content-type` for a body-carrying delivery. |
| **C-04** | **NodeInfo is served but its `openRegistrations` / `usage` values are host-seeded, not derived.** The values reflect the host's `ActivityPubServerOptions`; there is no live accounting of registrations/usage. | NodeInfo 2.0 | **Low** | **Acceptable** — NodeInfo is informational; live accounting is a host concern. |
| **C-05** | **The `ClientToServer` profile signs only `(request-target) host date`** — no `digest`/`content-type` (correct for a bodyless or browser-originated request). The `ServerToServer` profile adds `digest` + `content-type`. | draft-cavage-03 | **Low** | **Conformant** — the two profiles match the spec's intent (browser vs server-to-server). |
| **C-06** | **`@context` is always the default ActivityStreams context** — Iris never emits a non-default `@context` (Rule 10). Iris-namespaced terms (`iris:capabilities`) are full IRIs, so no `@context` change is needed. | AS2.0 / JSON-LD | **Low** | **Conformant** — adding an Iris `@context` would be a future option (Rule 10 permits it, documented why). |
| **C-07** | **Deterministic activity `Id`s** (e.g. `{actor}/follows/{target}`, content-hash `Create` ids) are not spec-mandated but are used for idempotent dedupe on the receiver. | AP (no mandate on id format) | **Low** | **Deliberate** — a receiver that dedupes on `id` is more robust; the ids are valid IRIs. No conformance risk. |
| **C-08** | **The `privateKey` property on the authenticated actor doc is non-standard** (served only to the owner with `Cache-Control: no-store`). | AP (no `privateKey` in the spec) | **Low** | **Deliberate extension** (Decision #1/#2) — a private, owner-only field; does not affect the public document's conformance. |

## 4. Prioritized fix plan (drives "Implement high-priority gaps")

Ranked by (a) spec-mandated vs nice-to-have, (b) interop impact, (c) effort. ★ = spec-mandated.

### Wave 1 — spec-mandated & interop-critical (do these first)

1. ~~**F-01 `sharedInbox`** (★, Blocker, M) — serve `endpoints.sharedInbox` on the actor/community doc **and** deliver to a remote actor's `sharedInbox` when advertised (falling back to the per-actor inbox). *Unblocks* realistic fan-out and is the single most-likely Phase 13 blocker.~~ **✅ Done (Slice 12.2).**
2. **F-05 EdDSA** (★, High, M) — add Ed25519 to `KeyPairGenerator` / `KeyPair` / `HttpSignatureSigner` / `HttpSignatureVerifier` + `AlgorithmFromPem`. *Unblocks* Pleroma federation.
3. **F-02 `Update`** (★, High, S) — a `UpdateActivityHandler` that refreshes the stored object. *Unblocks* profile/post editing propagation.
4. **F-03 `Delete`** (★, High, S) — a `DeleteActivityHandler` that removes the object and serves a `Tombstone` (F-10). *Unblocks* deletion propagation.
5. **F-08 `Move`** (High, S) — a `MoveActivityHandler` that re-points the follow edge and re-resolves the key (pairs with F-25). *Unblocks* user migration.

### Wave 2 — high-value, widely-expected

6. **F-14 followed feed** (Medium, L) — the home timeline: pull each followed remote actor's outbox via `IRemoteCollectionFetcher` and merge. *The most-used feature*; large but the highest user-value.
7. **F-12 `tag`/`attachment`/`inReplyTo`** (Medium, M) — thread replies, link mentions, render attachments. *Makes a feed usable.*
8. **F-04 `Like`** (High, M) — a `LikeActivityHandler` + `liked` collection endpoint.
9. **F-13 global search / directory** (Medium, M) — instance-wide search.
10. **F-22 delivery retry / dead-letter** (Medium, M) — persistent at-least-once delivery for production.

### Wave 3 — conformance completeness & moderation

11. **F-07 moderation (`Block`/`Mute`/`Flag`)** (High, L) — pairs with the Slice 11.10 `manuallyApprovesFollowers` gate into a full moderation surface.
12. **F-06 relay / `star`** (High, L) — relay support.
13. **F-09 `Add`/`Remove`** (Medium, S) — collection-modification primitives.
14. **F-11 `Article`/media object handling** (Medium, M).
15. **F-20 OAuth2** (Medium, L) — the real-client auth path (pairs with J-1's in-memory key).
16. **F-25 `key` rotation / `Move` re-resolution** (Medium, M).

### Wave 4 — low / nice-to-have

17. **F-15 outbound `Announce`** (Low, S) · **F-16 membership primitives** (Medium, S) · **F-17 intransitive activities** (Low, S) · **F-18 unordered `Collection`** (Low, S) · **F-19 typed `DeliverAsync` result** (Medium, S) · **F-21 key-rotation invalidation** (Medium, S) · **F-23 `FeedFilter`** (Low, S) · **F-24 community `followers` doc** (Medium, S) · **F-26…F-31** (Low, S).

### Conformance test suite (regression-protection)

For each Wave 1/2 item, add an integration test asserting the spec-required **wire format, headers, status codes, and pagination semantics** (per the Phase 12 "Conformance test suite" bullet). Concretely:
- `sharedInbox`: the actor doc's `endpoints.sharedInbox` is present; a delivery to a remote `sharedInbox` is signed correctly.
- `Update`/`Delete`/`Move`: the stored object is refreshed / tombstoned / re-pointed after the activity arrives (a real signed delivery over the in-process server).
- EdDSA: a signed round-trip with an Ed25519 key (sign → verify) + a PKIX Ed25519 `publicKeyPem` resolves on the actor doc.
- `content-type` in the `ServerToServer` signature base (C-03): assert the outbound delivery's signature base includes `content-type`.
- `Tombstone`: a deleted object is served as `{"type":"Tombstone","id":…}`.

## 5. Fold-back of the Phase 0 spec-research findings

The Phase 0 "Spec research" directive (ARCHITECTURE.md §Spec Research) had no concrete findings captured. **This inventory *is* the fold-back** — it systematically checks the implementation against ActivityPub, AS2.0, draft-cavage-03, RFC 8615, and NodeInfo 2.0, and records every gap (F-01…F-31) and deviation (C-01…C-08). The ROADMAP Phase 12 bullet "Fold the carried-forward spec-research findings (from Phase 0) into this audit's output" is therefore satisfied by this document.

## 6. Open questions (to resolve as the waves land)

1. **`sharedInbox` fan-out model** (F-01): deliver to *one* `sharedInbox` (the spec's intent) or keep per-follower fan-out as a fallback? (Spec: one shared inbox; the per-follower path is the cost it avoids.)
2. **Followed-feed freshness** (F-14): pull-on-read (a fresh fetch per timeline view) vs a background poller (a `Channel<T>` + `IHostedService` per the coding style)? The pull-on-read path is simpler and matches the read-through cache pattern.
3. **Moderation storage** (F-07): a dedicated `IModerationStore` (block/mute/flag sets) vs reusing `ExtensionData` on the actor? A dedicated store is cleaner and testable.
4. **`Tombstone` retention** (F-03/F-10): how long does Iris keep a `Tombstone` before hard-deleting? (Affects outbox size.)
