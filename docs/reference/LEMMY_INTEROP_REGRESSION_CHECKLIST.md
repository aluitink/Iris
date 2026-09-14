# Lemmy Interop Regression Checklist (Phase 136)

> The standing regression checklist for the Phase 136 Lemmy interop work. Each item maps to a
> verified in-process two-instance integration test (the authoritative source) and, where applicable,
> a live wire check. The checklist is **repeatable**: run it after any code change that touches
> federation delivery, signature validation, community peering, or moderation, and before declaring a
> release candidate. A clean sweep = every item passes or is explicitly recorded as a known gap.
>
> **Prerequisites:** the solution builds clean (`dotnet build`), the full test suite passes
> (`dotnet test`), and (for live checks) the compose stack is up and healthy with the public FQDNs
> resolving. The live Iris↔Lemmy leg is **expected-blocked** by the Lemmy-side signature-parse gap
> (136.3) and the WebFinger/egress gap (136.2) — both ops/Lemmy-side, not Iris code gaps.

## How to use this checklist

1. **Automated (primary gate):** run `dotnet test`. Every row in §1–§8 that has a test reference must
   pass. This is the fast, repeatable regression gate — no manual intervention needed.
2. **Live (secondary gate):** for the rows marked **LIVE**, execute the wire check against a running
   two-instance topology (Docker compose) or a real Lemmy instance. The live Iris↔Lemmy leg is
   expected-blocked (see §10); record the blocker evidence (trace ID, HTTP status, error message).
3. **Record outcomes** in the pass/fail matrix (§9) for the release slice. A "stuck state" (live
   blocker) is a valid outcome when the blocker is a documented ops/Lemmy-side gap.

## 1. Discovery (136.2)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| D1 | WebFinger resolves `@alice@iris-host` to the actor document | `ServerEndpointIntegrationTests` (136.2) | `GET /.well-known/webfinger?resource=acct:alice@iris-host` returns 200, `application/ld+json`, `subject` = actor IRI |
| D2 | WebFinger resolves a community handle `!iris@iris-host` to the Group document | `ServerEndpointIntegrationTests` (136.2) | `GET /.well-known/webfinger?resource=acct:!iris@iris-host` returns the Group document (`type: "Group"`) |
| D3 | Actor document served with `application/activity+json` content type | `ServerEndpointIntegrationTests` (136.2) | `GET /ap/v1/u/alice` returns 200, `Content-Type: application/activity+json` |
| D4 | Content negotiation: `Accept: application/ld+json` returns ld+json | `ServerEndpointIntegrationTests` (136.2) | `GET /ap/v1/u/alice` with `Accept: application/ld+json` returns 200, `Content-Type: application/ld+json` |
| D5 | NodeInfo 2.0 document served | `ServerEndpointIntegrationTests` (136.2) | `GET /ap/v1/nodeinfo/2.0` returns 200, valid NodeInfo 2.0 JSON |
| D6 | NodeInfo discovery link present | `ServerEndpointIntegrationTests` (136.2) | `GET /.well-known/nodeinfo` returns the `links` array with the 2.0 self link |
| D7 | Remote actor document fetch resolves the key (JWK) | `FederationTraceIntegrationTests` (136.1) | Fetching a remote actor document populates the `RemoteActorCache` with the signing key |

**LIVE D8:** Lemmy resolves `@alice@iris-host` via WebFinger. *Expected-blocked* (136.2 ops finding:
504 on the reverse-proxy egress path). Record the HTTP status and error.

## 2. Authentication / Signatures (136.3)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| A1 | Valid RSA-SHA256 signed POST is accepted | `FederationSignatureIntegrationTests` (136.3) | `POST /inbox` with a valid `Signature` header (RSA, `draft-cavage`) returns 202 |
| A2 | Unsigned POST to inbox is rejected (401) | `FederationSignatureIntegrationTests` (136.3) | `POST /inbox` without a `Signature` header returns 401 |
| A3 | Invalid signature (wrong key) is rejected (401) | `FederationSignatureIntegrationTests` (136.3) | `POST /inbox` with a signature from an unknown key returns 401 |
| A4 | Tampered body (digest mismatch) is rejected (401) | `FederationSignatureIntegrationTests` (136.3) | `POST /inbox` with a valid signature but a modified body returns 401 |
| A5 | ECDSA P-256 signed POST is accepted | `FederationSignatureIntegrationTests` (136.3) | `POST /inbox` with an ECDSA P-256 signature returns 202 |
| A6 | EdDSA (Ed25519) signed POST — known gap | `FederationEd25519SignatureIntegrationTests` (136.3) | **GAP (SIG2):** EdDSA-signed POST is rejected. Documented; not an Iris defect for Lemmy interop (Lemmy uses RSA) |
| A7 | Signature base string: no trailing newline, `sha-256` digest | `FederationSignatureIntegrationTests` (136.3) | The signature base matches the Fediverse de-facto convention (headers joined with `\n`, no trailing `\n`) |
| A8 | GETs are not signature-validated (POST-only) | `FederationSignatureIntegrationTests` (136.3) | `GET /ap/v1/u/alice` without a signature returns 200 (not 401) |
| A9 | Lemmy PKIX key format accepted in actor document | `ServerEndpointIntegrationTests` (136.2) | An actor document with a PKIX (DER) public key is resolved and used for validation |

**LIVE A10:** Lemmy validates an Iris outbound signed POST. *Expected-blocked* (136.3: Lemmy's
`http-signatures` parser rejects the signature format — `Error when parsing signature from Http
Signature` → 400). Lemmy-side gap, not an Iris defect. Record the trace ID and HTTP status.

## 3. Peering / Community Follow (136.4)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| P1 | Person follows person (auto-accept): `Accept` delivered to follower's inbox | `CrossInstanceAcceptPropagationIntegrationTests` (26.1) | `POST /inbox` with a `Follow` from bob → alice's `Accept` delivered to bob's inbox; bob's `following` includes alice; alice's `followers` includes bob |
| P2 | Person follows person (gated): no `Accept` until operator approves | `CommunityGatedPeeringIntegrationTests` (136.4, person direction) | `Follow` received; no `Accept` sent; provisional edge recorded; operator publishes `Accept` → edge finalized + `Accept` delivered |
| P3 | Community follows person: `Accept` delivered | `CommunityGatedPeeringIntegrationTests` (136.4, community direction) | `Follow` from a Group to a Person → `Accept` delivered to the Group's inbox |
| P4 | Person follows community: `Accept` delivered, edge recorded | `CommunityGatedPeeringIntegrationTests` (136.4) | `Follow` from a Person to a Group → `Accept` delivered; Group's `members`/`followers` includes the Person |
| P5 | Gated community follow: operator approves → `Accept` + edge finalized | `CommunityGatedPeeringIntegrationTests` (136.4, gated direction) | `Follow` to a gated community → no `Accept` until operator publishes `Accept` to the outbox; then `Accept` delivered to the follower's inbox |
| P6 | Reject: operator publishes `Reject` → edge removed, `Reject` delivered | `CrossInstanceRejectPropagationIntegrationTests` | `Reject` published to outbox → provisional edge removed; `Reject` delivered to the would-be follower's inbox |
| P7 | Unfollow: `Undo` of `Follow` → edge removed on both sides | `PersonFollowsPersonUnfollowPropagationIntegrationTests`, `CommunityFollowsCommunityUnfollowPropagationIntegrationTests` | `Undo(Follow)` delivered to the other instance → edge removed on both sides |
| P8 | Mutual follow delivery loop: A follows B, B follows A — both `Accept`s delivered | `MutualFollowDeliveryLoopIntegrationTests` | Both instances record the edge; both `Accept`s are delivered; no infinite loop |

**LIVE P9:** Lemmy community follows an Iris community. *Expected-blocked* (135.1b(3): Lemmy's
`/api/v3/community/follow` is user-follows-community, signed by the user — the reverse direction
[Lemmy community follows Iris community] is not cleanly drivable). Record the attempted endpoint and
response.

## 4. Delivery (136.11, 136.12)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| DEL1 | Outbound post (Create) delivered to all remote followers' inboxes | `PostFederationIntegrationTests`, `OutboxPublishServerDeliversIntegrationTests` | `Create` published to outbox → delivery job created per remote follower → signed `POST` to each follower's inbox; receiver stores the activity |
| DEL2 | Delivery retry on 5xx: exponential backoff, max 5 attempts | `DeliveryReliabilityIntegrationTests` (136.11) | 5xx response → retry with backoff (1s, 2s, 4s, 8s); 5th failure → dead-lettered with correct inbox, actor, kind, detail, attempt count |
| DEL3 | Delivery permanent failure on 4xx: no retry, immediate dead-letter | `DeliveryReliabilityIntegrationTests` (136.11) | 4xx response → job dead-lettered after 1 attempt (no retry) |
| DEL4 | `Retry-After` delay-seconds honored | `DeliveryReliabilityIntegrationTests` (136.11) | 429 with `Retry-After: 3` → worker waits ≥ 3s before retry |
| DEL5 | `Retry-After` HTTP-date honored | `DeliveryReliabilityIntegrationTests` (136.11) | 429 with `Retry-After: <HTTP-date>` (3s in the future) → worker waits until the specified date (gap ≥ 2000ms) |
| DEL6 | Idempotency: re-POSTed delivery stored exactly once | `DeliveryReliabilityIntegrationTests` (136.11) | A delivery POSTed twice (simulating at-least-once retry) → the activity is stored once (C-07: `TryAddActivityAsync` dedupes by IRI) |
| DEL7 | End-to-end dead-lettering in a real two-instance topology | `DeliveryDeadLetterIntegrationTests` (136.11, unskipped) | A's delivery to B's inbox always fails (500) → worker retries (5 attempts) → job dead-lettered with correct metadata |
| DEL8 | Circuit breaker: repeated failures to a host trip the breaker | `DeliveryReliabilityIntegrationTests` (136.11) | After N consecutive failures to a host, the breaker opens; subsequent deliveries to that host are short-circuited |
| DEL9 | `/likes` endpoint: single activity-table sweep (not per-liker) | `InteractionCollectionIntegrationTests` (136.12) | `GET {object}/likes` with 2+ likers whose activities are stored → returns full Like documents (minted id + actor + object); single `GetAllActivitiesAsync` sweep (F-136.12.8) |
| DEL10 | `/shares` endpoint: single activity-table sweep (not per-announcer) | `InteractionCollectionIntegrationTests` (136.12) | `GET {object}/shares` with 2+ announcers whose activities are stored → returns full Announce documents; single sweep |
| DEL11 | Relay delivery: Create/Announce delivered to configured relays | `UpdateDeleteRelayFanOutIntegrationTests` (28.2) | Delivery jobs created for each configured relay; signed `POST` to each relay's inbox |

## 5. Threading / Replies (136.7)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| T1 | Reply to a local parent: `Create` with `inReplyTo` delivered to parent author | `CrossInstanceReplyThreadIntegrationTests` (136.7) | Reply published → `Create` delivered to the parent author's inbox; `inReplyTo` = parent IRI; parent's `/replies` collection includes the reply |
| T2 | Reply to a **remote** parent: federates to the parent's home instance | `CrossInstanceReplyThreadIntegrationTests` (136.7) | Reply to a remote parent → `Create` delivered to the parent's home instance; `inReplyTo` survives the cross-origin hop; the remote parent's `/replies` includes the reply |
| T3 | `conversationId` anchored to the parent IRI (stable thread root) | `CrossInstanceReplyThreadIntegrationTests` (136.7) | Reply's `conversationId` = parent IRI (the stable thread root), not the reply's own IRI |
| T4 | Deep thread: nested reply (reply-to-reply) threads correctly | `CrossInstanceReplyThreadIntegrationTests` (136.7) | A reply to a reply → `inReplyTo` = the intermediate reply's IRI; the thread structure is preserved |
| T5 | `/replies` collection served for a remote parent with reply edges | `CrossInstanceReplyThreadIntegrationTests` (136.7) | `GET {remote-parent}/replies` returns the reply collection (the instance knows the reply edges even though the parent object was never stored locally) |
| T6 | Thread pagination: deep threads page correctly | `CrossInstanceReplyThreadIntegrationTests` (136.7) | `GET {parent}/replies?page=2&limit=N` returns the correct page (no duplicates, no gaps) |

## 6. Lifecycle / Content Propagation (136.5, 136.6, 136.8, 136.9)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| L1 | Inbound Create to community: timestamp preserved, stored once (C-07) | `CommunityInboundContentIntegrationTests` (136.5) | Remote `Create` delivered to a community inbox → the object is stored with the originator's timestamp (not the receive time); re-delivery does not create a duplicate (C-07 idempotency) |
| L2 | Outbound post from a community: federates with community identity intact | `CommunityOutboundContentIntegrationTests` (136.6) | A community-attributed post → `Create` delivered to remote followers; the remote instance persists the Group document (community identity survives the hop) |
| L3 | Like: federated to the object's author (remote) | `LikeAnnounceUndoPropagationIntegrationTests` (136.8) | Local Like of a remote object → `Like` delivered to the object's author's inbox; the author's instance records the like edge; the object's `/likes` counter increments |
| L4 | Undo(Like): removes the like edge on the remote instance | `LikeAnnounceUndoPropagationIntegrationTests` (136.8) | `Undo(Like)` delivered to the object's author's inbox → the like edge is removed; the `/likes` counter decrements |
| L5 | Announce (boost) of a remote object: federates to the object's home | `CrossInstanceAnnounceIntegrationTests` (136.8) | Local Announce of a remote object → `Announce` delivered to the object's home instance; the home instance's `/shares` counter increments |
| L6 | Undo(Announce): removes the boost edge on the remote instance | `LikeAnnounceUndoPropagationIntegrationTests` (136.8) | `Undo(Announce)` delivered to the object's home → the announce edge is removed; the `/shares` counter decrements |
| L7 | Update (edit): federated to remote followers + relays; remote copy refreshed | `UpdatePropagationIntegrationTests` (27.1) | `Update` published → delivered to all remote followers' inboxes + relays; the remote instance updates the stored object |
| L8 | Delete: federated + tombstones the remote copy (AS2.0 `Tombstone`) | `UpdateDeleteRelayFanOutIntegrationTests` (28.2) | `Delete` published → delivered to remote followers + relays; the remote instance stores a `Tombstone` (the object is no longer fetchable by IRI) |
| L9 | Delete parent: thread collapses on every instance (no orphaned replies) | `CrossInstanceDeleteThreadCollapseIntegrationTests` (136.9) | Deleting a parent post → the parent's `/replies` collection is empty on the home and every remote instance (child objects remain stored, fetchable by direct IRI; only the thread listing is collapsed) |
| L10 | Move (actor migration): inbound `Move` updates the actor IRI | `MoveFederationIntegrationTests` (12.4) | `Move` activity delivered → the actor's IRI is updated; old content references the new IRI |

## 7. Moderation / Trust Boundary (136.10)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| M1 | Local actor blocks remote actor: edge recorded, content excluded from feed | `CrossInstanceBlockedContentIntegrationTests` (136.10) | `Block` from alice (A) to bob (B) → bob's existing content excluded from alice's feed; bob's new content (published after the block) is **stored** (fetchable by direct IRI) but **excluded from alice's feed** (the trust-boundary guarantee: the `FeedService` applies the block edge on the reader's side) |
| M2 | Remote actor blocks local actor: reverse-direction block recorded | `CrossInstanceBlockedContentIntegrationTests` (136.10) | `Block` from bob (B) to alice (A) → the `bob→alice` edge recorded on A; `GetBlockersAsync(alice)` includes bob; alice's content excluded from bob's feed on B |
| M3 | Undo(Block): removes the block edge on the remote instance | `CrossInstanceBlockedContentIntegrationTests` (136.10) | `Undo(Block)` delivered → the block edge is removed; the previously-blocked content is visible again |
| M4 | Report/Flag: moderation signal recorded, propagated | `CrossInstanceBlockedContentIntegrationTests` (136.10) | `Report`/`Flag` activity delivered → the moderation edge is recorded on the target's home instance |
| M5 | Blocked content not reintroduced via new delivery (trust boundary) | `CrossInstanceBlockedContentIntegrationTests` (136.10) | After alice blocks bob, bob publishes a new post → the post is delivered to A and stored, but alice's feed does not include it (the delivery suppression is a local optimization; the authoritative guarantee is the reader-side block edge) |

## 8. Observability / Reliability (136.1, 136.11, 136.12)

| # | Check | Test reference | Pass criteria |
|---|---|---|---|
| O1 | Federation trace: inbound POST captured (actor, kind, inbox, status) | `FederationTraceIntegrationTests` (136.1) | `POST /inbox` → trace entry recorded with actor IRI, activity kind, inbox IRI, HTTP status, timestamp |
| O2 | Federation trace: outbound delivery captured (actor, kind, target, status) | `FederationTraceIntegrationTests` (136.1) | Outbound delivery → trace entry recorded with actor IRI, activity kind, target inbox IRI, HTTP status, attempt count |
| O3 | Trace endpoint: `GET /local/v1/federation-trace` returns the trace | `FederationTraceEndpointTests` (136.1, Web) | `GET /local/v1/federation-trace` returns 200, JSON array of trace entries |
| O4 | Trace endpoint: queryable by actor/kind/status filters | `FederationTraceEndpointTests` (136.1, Web) | `GET /local/v1/federation-trace?actor=...&kind=...&status=...` returns filtered results |
| O5 | Delivery metrics: counter for delivery attempts (success/failure/dead-letter) | `DeliveryReliabilityIntegrationTests` (136.11) | `IrisDeliveryMetrics` counters increment correctly: `delivery.attempts` (total), `delivery.success`, `delivery.failure`, `delivery.dead_letter` |
| O6 | No read-side request metrics (known observability gap) | 136.12 audit (F-136.12.10) | **GAP (triaged):** no metrics for inbound GET requests (object fetch, collection pagination, `/likes`, `/shares`). Documented; follow-up work. |

## 9. Pass/Fail Matrix (release gate)

For each release slice, fill in the outcome per category. An item is **PASS** if the automated test
passes and (for LIVE items) the live wire check passes or is recorded as expected-blocked with
documented evidence. An item is **FAIL** if the automated test fails or the live wire check fails
without a documented blocker. An item is **GAP** if it is a known, documented, accepted limitation.

| Category | Items | PASS | FAIL | GAP | Notes |
|---|---|---|---|---|---|
| Discovery | D1–D8 | | | | |
| Auth / Signatures | A1–A10 | | | | |
| Peering | P1–P9 | | | | |
| Delivery | DEL1–DEL11 | | | | |
| Threading | T1–T6 | | | | |
| Lifecycle | L1–L10 | | | | |
| Moderation | M1–M5 | | | | |
| Observability | O1–O6 | | | | |
| **Total** | **68 items** | | | | |

**Release gate rule:** a release is **GO** when every FAIL is empty (or each FAIL has a documented,
accepted, severity-annotated exception). A GAP is acceptable when it is documented in
[RISK_GAP_REGISTER.md](RISK_GAP_REGISTER.md) with a severity and workaround.

## 10. Known Incompatibilities and Lemmy-Specific Notes

These are the documented, accepted limitations and Lemmy-specific behaviors that affect interop.
Each has a severity and a workaround (or "none — accepted limitation").

| # | Issue | Severity | Workaround | Source |
|---|---|---|---|---|
| K1 | Lemmy rejects Iris outbound signatures (Lemmy-side `http-signatures` parser is stricter than draft-cavage) | **High** (blocks live Lemmy↔Iris delivery) | None — do not change Iris's format to appease one platform's parser (would break other platforms). Lemmy-side fix or ops escalation. | 136.3 |
| K2 | Lemmy→Iris WebFinger fails live (504 on the reverse-proxy egress path) | **High** (blocks live Lemmy→Iris discovery) | Ops: fix the reverse-proxy egress configuration for the `/.well-known/webfinger` path. | 136.2 |
| K3 | Iris site root `/` serves HTML SPA, not a JSON-LD `DiasporaFederated` doc | **Medium** (blocks instance-level federation; community-level is unaffected) | Serve a JSON-LD document at `/` or `/.well-known/diaspora-federated`. Follow-up work. | 136.3 |
| K4 | Lemmy community is not a pure AS 2.0 `Group` (has `t:`/`c:` IRIs, different follow flow) | **High** (group-interop scenarios may not map 1:1) | Use user (non-community) interop as the safer Lemmy path. User-level scenarios (F, C, T, A, M) are the reliable path. | U-2 (RISK_GAP_REGISTER) |
| K5 | Lemmy community-follow has no clean REST endpoint for the reverse direction (Lemmy community follows Iris community) | **Medium** (one-directional community peering) | Drive the follow from the Iris side (Iris community follows Lemmy community) — this direction works. | 135.1b(3) |
| K6 | Lemmy vote scores (`score`/`upvotes`/`downvotes`) are not in AP documents (only in Lemmy's private REST API) | **Low** (accepted limitation) | None — vote scores are not part of the AP wire format. UI should not display Lemmy-specific vote counts for remote objects. | 78.1/79.2 |
| K7 | Lemmy Person docs lack `name`/`summary`/`icon` in the AP document | **Low** (accepted limitation) | UI shows handle + fallback avatar for remote Lemmy persons. | 78.1/79.2 |
| K8 | Lemmy posts as `Page` (rejects a `Note` carrying `inReplyTo`); Iris must post as a Lemmy-compatible `Page` with `attributedTo:[author,community]`, `to:[community,as#Public]`, `cc:[followers]`, `content`=HTML, `source`={markdown} | **Medium** (wire-format adaptation required) | Iris's Lemmy-compatible `Page` wire format (118.2) handles this. Verified in 136.5/136.6. | 118.2 |
| K9 | nginx reverse proxy 400s external POST federation paths | **Medium** (ops environment issue) | Hit the Iris container directly on its Docker-network IP to bypass nginx. | 135.1b(3) |
| K10 | No EdDSA (Ed25519) signature validation in Iris | **Low** (Lemmy uses RSA; some Pleroma/Akkoma configs use EdDSA) | SIG2 gap — documented. Follow-up: add EdDSA support. | SIG2 (COMPATIBILITY_MATRIX) |

## 11. Regression harness — automated gate

The **primary** regression gate is the automated test suite. The following test classes are the
authoritative source for each checklist category:

| Category | Test classes (authoritative) |
|---|---|
| Discovery | `ServerEndpointIntegrationTests` (136.2), `FederationTraceIntegrationTests` (136.1) |
| Auth / Signatures | `FederationSignatureIntegrationTests` (136.3), `FederationEd25519SignatureIntegrationTests`, `KeyRotationFederationIntegrationTests` |
| Peering | `CrossInstanceAcceptPropagationIntegrationTests` (26.1), `CommunityGatedPeeringIntegrationTests` (136.4), `CrossInstanceRejectPropagationIntegrationTests`, `PersonFollowsPersonUnfollowPropagationIntegrationTests`, `CommunityFollowsCommunityUnfollowPropagationIntegrationTests`, `MutualFollowDeliveryLoopIntegrationTests` |
| Delivery | `PostFederationIntegrationTests`, `OutboxPublishServerDeliversIntegrationTests`, `DeliveryReliabilityIntegrationTests` (136.11), `DeliveryDeadLetterIntegrationTests` (136.11), `UpdateDeleteRelayFanOutIntegrationTests` (28.2), `InteractionCollectionIntegrationTests` (136.12) |
| Threading | `CrossInstanceReplyThreadIntegrationTests` (136.7) |
| Lifecycle | `CommunityInboundContentIntegrationTests` (136.5), `CommunityOutboundContentIntegrationTests` (136.6), `LikeAnnounceUndoPropagationIntegrationTests` (136.8), `CrossInstanceAnnounceIntegrationTests` (136.8), `UpdatePropagationIntegrationTests` (27.1), `UpdateDeleteRelayFanOutIntegrationTests` (28.2), `CrossInstanceDeleteThreadCollapseIntegrationTests` (136.9), `MoveFederationIntegrationTests` (12.4) |
| Moderation | `CrossInstanceBlockedContentIntegrationTests` (136.10) |
| Observability | `FederationTraceIntegrationTests` (136.1), `FederationTraceEndpointTests` (136.1, Web) |

**Command:** `dotnet test` (all projects). A clean build + full test pass is the automated gate.

**Live interop (secondary gate):** `tests/Iris.LiveInterop.Tests/` is gated by `IRIS_LIVE_INTEROP=1`.
The live scenarios (F1, C1, SIG1, P1) are stubs — fill in the target IRIs and execute against a
running Lemmy instance. The live Iris↔Lemmy leg is expected-blocked (K1, K2) until the Lemmy-side
signature-parse and egress gaps are resolved.
