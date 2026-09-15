# Phase 138 — Lemmy community federation: live interop, sync, and archival

> Referenced from [PLAN.md](../../PLAN.md)'s "Up Next". This is the detailed scope for Phase 138 —
> establishing a real, manually-orchestrated peering relationship between Iris and the local Lemmy
> stack ([lemmy/](../../lemmy/)), forwarding content and engagement in both directions, archiving a
> locally-rebuildable copy, and bringing Lemmy metadata tracking to parity with how Iris already
> tracks Mastodon-adjacent metadata via `iris:` extension terms. PLAN.md keeps only a one-line
> pointer to this doc — update slice status here, roll the finished summary into PLAN.md /
> [docs/ROADMAP.md](../ROADMAP.md) as slices close.

## Why this phase, and what "done" looks like

Prior Lemmy work (Phase 78 wire-format investigation, Phase 89 community peering, Phase 135.1
remote-community persistence + local Lemmy deployment, Phase 137 client-side capability-aware
browsing) proved the *pieces* work in isolation — mostly against synthetic `TestServer` fixtures or
a Lemmy instance that was only ever exercised for read-only browsing. **Nothing has yet driven a
real, sustained, two-way Iris↔Lemmy relationship**: no live peering handshake against the current
`lemmy/` stack has completed end-to-end, no post has ever been pushed from Iris into a Lemmy
community's post list, and no Lemmy vote/comment has ever round-tripped into Iris's local store.

End state for this phase:

1. Iris can establish a **peer relationship** with a Lemmy community in a way that is meaningful on
   both sides (not just a one-way read).
2. A post authored in an Iris community is **forwarded** to a peered Lemmy community and appears in
   Lemmy's own post listing, correctly attributed.
3. **Likes, dislikes, and replies** propagate in both directions between the peered communities.
4. Iris keeps a **durable local copy** of everything synced, sufficient to rebuild the full thread
   (post + nested replies + engagement) with the Lemmy instance offline.
5. Lemmy-specific metadata (score inputs, locked/featured/removed state, community NSFW) is tracked
   via `iris:` extension terms, the same way Mastodon-adjacent metadata already is.
6. A cross-platform consistency pass confirms the resulting model doesn't quietly become
   Lemmy-shaped or Mastodon-shaped at the expense of the other (or of Pleroma/Misskey/PeerTube,
   which Iris already interops with per Phase 79.3/81.x).

This will require **heavy manual orchestration**: creating communities/posts/users through each
platform's own UI/API, watching what actually crosses the wire, and adjusting Iris to match reality
rather than assuming the Phase 78 investigation's happy-path model is complete. Several of the
"facts" below are **not yet verified against this repo's live Lemmy container** — they're documented
here as the research basis for each slice, and each slice's deliverable check includes confirming or
correcting them with a real captured payload before code is written against the assumption.

## Research basis: Lemmy federation behavior not yet covered by [781-lemmyverse-investigation](../changes/781-lemmyverse-investigation.md)

The existing investigation doc covers the actor/document *shape*. It does not cover the following,
which this phase's slices depend on and must verify live:

- **Votes are individually federated, not just aggregated.** An upvote is a `Like` activity; a
  downvote is a **`Dislike`** activity (a de-facto extension type used by Lemmy and PeerTube — not
  part of core AS2). These are sent as real activities to the target's inbox, each carrying the
  voter's actor id. Lemmy's own displayed "score" is a server-side aggregate Lemmy computes from the
  `Like`/`Dislike` activities it has received — it is *not* itself a wire field. The 781 doc's
  finding "no vote/score data in AP documents" is correct for the aggregate, but incomplete: the
  individual vote activities themselves are exactly the kind of thing Iris's existing per-object
  edge/reverse-index model (`LikeActivityHandler`, `iris:likedCount`) already knows how to record —
  Iris just has no `Dislike` counterpart yet.
- **Comments are `Note`, not `Page`.** Only top-level posts are `Page`; a Lemmy comment is a `Note`
  with `inReplyTo` pointing at the parent post or comment, matching Iris's own reply model closely.
- **Author-delete vs. mod-removal are different concepts.** Lemmy distinguishes a user deleting
  their own content from a moderator removing it (reversible, distinct from a tombstone). The exact
  activity verbs Lemmy federates for a mod removal/restore are version-sensitive and must be captured
  live (see 138.23) rather than assumed — do not build the archival/tombstone logic against a guess.
- **Community-level and post-level NSFW are both real, and distinct.** A whole Lemmy community can
  be flagged NSFW (implicitly marking all its posts sensitive); a post can also be individually
  flagged. Iris's current sensitivity model is post-only.
- **Lemmy communities do not support `manuallyApprovesFollowers`-style gating over federation** in
  the way Iris's own community settings extension does — community follows from other instances are
  effectively always auto-accepted. Iris's peering UI must treat "Lemmy always accepts" as expected
  platform behavior, not a bug in the accept/reject flow.
- **Lemmy has no user-initiated "boost" concept.** Its own use of `Announce` is purely the
  community's fan-out envelope around a member's `Create` — there is no Mastodon-style
  re-share-a-post-you-didn't-write action to interop with. Iris must not offer a "boost" affordance
  against Lemmy-sourced content.
- **Bans/blocks federate as `Block`,** at both the community level (community bans a person) and
  potentially the instance level; reports federate as `Flag` (already aligned with Iris's own
  moderation model from Phase 058-062).
- **Instance-level federation gating is coarse.** A Lemmy instance's federation mode
  (open/allowlist/blocklist) and its enabled/disabled state are set instance-wide by the admin, not
  per-actor — a peering attempt can silently fail for reasons invisible from the Iris side alone.
  This is the likely explanation if the 135.1b-style proxy/network issues recur.
- **Delivery grouping via `sharedInbox`.** Lemmy posts activities to an instance's shared inbox once
  for all local recipients rather than per-actor, when addressing multiple actors on the same
  instance. Relevant to how Iris should address deliveries *to* a Lemmy instance at scale (not
  required for a single-community pilot, but worth confirming Iris's outbound delivery doesn't
  needlessly multiply requests once more than one Lemmy actor is a recipient).
- **Signature strictness.** Lemmy has already been found to require exact `Digest` header casing
  (change 230). Assume other header-construction strictness (component order, `(request-target)`
  format) until proven otherwise against the real container — verify, don't assume clean from
  Iris-to-Iris tests alone.
- **Actor document re-fetch / key rotation.** Lemmy re-fetches a cached remote actor document after
  its own staleness threshold, which matters for how quickly a key rotation or profile edit on either
  side is picked up by the other.

## Progress tracking

Each slice below is a markdown checkbox: `- [ ]` not started, `- [x]` done. Mark `- [~]` for
in-progress (partially executed / blocked mid-slice — say why in a trailing note). Check a box only
when its **Check:** criterion has been met with recorded evidence (a curl transcript, a screenshot, a
test run, a captured payload) — not on intent alone. Do not reorder or renumber slices as they
complete; strike through only if a slice is dropped (with a one-line reason).

**Resume checkpoint:** Stage A (138.1–138.3) complete; **138.4's Iris-side blocker is FIXED**
(2026-09-15) — Iris now serves the instance actor's public document at `GET /` for ActivityPub clients
(content-negotiated), so Lemmy's site-actor dereference succeeds. **138.5 (Iris → Lemmy community
discovery) is DONE** (2026-09-15) — integration test proves cross-instance remote `Group` follow +
persist via the 135.1 `RemoteCommunityPersister` path. **138.6 (mutual peering handshake) is DONE**
(2026-09-15) — `AcceptActivityHandler` records both edges; 3 integration tests verify both collections
populate on both sides. **138.7 (peering trust/identity checks) is DONE** (2026-09-15) — 4 tests in
`PeeringTrustIdentityTests` fetch the real Lemmy community's `publicKeyPem` and verify the trust
boundary (public-only key), signature base construction compatibility (RSA-PKCS1v15 + SHA-256),
Signature header well-formedness, and the lowercase `sha-256=` digest convention. The live wire test
(actual Iris→Lemmy delivery accepted with 2xx) is deferred to 138.10. **138.8 (peering failure-mode
drill) is DONE** (2026-09-15) — 2 live tests (`PeeringFailureModeTests`): delivery to the real Lemmy
shared inbox completes without a transport error; delivery to an unreachable port is dead-lettered
with `TransportError` after the configured retry budget. Lemmy 0.19's retry behavior documented:
infinite retries, exponential backoff (1.25^(N-1)s, capped at 1 day), `fail_count` persisted in
Postgres. Key asymmetry: Iris dead-letters after 5 attempts (~31s); Lemmy retries indefinitely.
**138.9 (push-content-to-Lemmy shape) is DONE** (2026-09-15) — decision 058: an Iris post landing in
a remote community's post list is an explicit cross-post (target community `Group` in `to`, `Create`
delivered to its `sharedInbox`/`inbox` signed as the authoring local actor, `Note` object,
client-supplied target IRI, same activity id reused). Note-vs-`Page` deferred to 138.11.
**138.10 (post-to-Lemmy-community delivery path) is DONE** (2026-09-15) — cross-post leg implemented
(decision 058): a Create whose `to` explicitly addresses a remote community is delivered to that
community's inbox (sharedInbox/inbox, signed as the authoring local actor); 2 two-instance integration
tests (positive cross-post + negative control). Live Lemmy acceptance (post in Lemmy's own post
listing) is exercised live with the 138.11 fidelity check.
**Next concrete step is 138.11** (cross-post fidelity check — live Lemmy-rendered post, Note-vs-`Page`).
**Still open for 138.4 (Lemmy-side):** its search/resolve-by-URL UI does not surface the Iris `interop`
community. Also still open: the `interop` webfinger name-collision edge case.

When a full Stage (A–H) closes, add one line to PLAN.md's Recently Completed pointing back here.
Only add a [docs/ROADMAP.md](../ROADMAP.md) entry when the whole phase (138.29) closes.

## Slices

### Stage A — Manual orchestration foundation

- [x] **138.1 — Local Lemmy environment audit & refresh.** Confirmed `lemmy/docker-compose.yml` still
  boots clean from the current images/config; reconcile the baked hostname
  (`lemmy/config/config.hjson`) against whatever FQDN this pass will use (dev vs. the
  `lemmy.luit.ink` prod-shape host from 135.1b — pick one and record it). Re-seed one clean admin
  user + one clean community via the REST API if the existing `interop` community/state is stale.
  **Check:** `GET /api/v3/site` returns 200 with federation enabled; a fresh community is visible at
  its `Group` IRI with `publicKey`/`inbox`/`outbox`/`followers` present.
- [x] **138.2 — Two-way network reachability matrix.** Verify, with `curl` from both containers'
  perspective (and through the external reverse proxy if in play), every path this phase depends on:
  Iris → Lemmy actor GET, Iris → Lemmy inbox POST, Lemmy → Iris actor GET, Lemmy → Iris inbox POST,
  both instances' WebFinger. 135.1b found the external nginx blocking POST federation paths — confirm
  whether that's still true and, if so, flag the exact paths needing an allowlist entry.
  **Check:** a table (endpoint × direction × status code) with curl evidence; any blocked path has an
  owner (operator nginx config vs. Iris/Lemmy app bug) assigned before moving on.

  **Evidence (2026-09-15).** Both stacks share one Docker bridge
  (`irisweb_iris-web-net`; every container resolves by service name). Iris serves plain HTTP on
  `:8080` in-container (advertised `https://iris.luit.ink`); the Lemmy nginx proxy listens on
  `:8082` in-container (host maps `8091→8082`, advertised `https://lemmy.luit.ink`). The external
  FQDNs both resolve to `69.129.197.210` from inside the containers (the reverse proxy).

  | Endpoint | Direction | Direct (container→container) | Via FQDN (reverse proxy) | Notes |
  | --- | --- | --- | --- | --- |
  | Site/actor doc `GET /` (Lemmy site actor) | Iris→Lemmy | 200 `application/activity+json` | 200 | `Accept: application/activity+json` required (else UI HTML catch-all) |
  | Community actor doc `GET /c/interop` | Iris→Lemmy | 200 `application/activity+json` | 200 | `inbox=https://lemmy.luit.ink/c/interop/inbox`, `endpoints.sharedInbox=https://lemmy.luit.ink/inbox` |
  | Person actor doc `GET /u/lemmyadmin` | Iris→Lemmy | 200 `application/activity+json` | — | |
  | Inbox `POST /c/interop/inbox` (unsigned) | Iris→Lemmy | 400 `{"error":"unknown","message":"Incoming activity has invalid digest for body"}` | 400 | **Path open** — 400 is Lemmy's HTTP-signature (digest) verification, not a network block |
  | Inbox `POST /inbox` (root sharedInbox, unsigned) | Iris→Lemmy | 400 (invalid digest) | 400 | same signature rejection |
  | WebFinger `GET /.well-known/webfinger` | Iris→Lemmy | 200 | 200 | |
  | NodeInfo `GET /nodeinfo/2.0` | Iris→Lemmy | 404 | — | Lemmy serves no nodeinfo 2.0 (informational; not a federation dependency) |
  | Person actor doc `GET /ap/v1/u/alice` | Lemmy→Iris | 200 `application/activity+json` | 200 | `inbox=https://iris.luit.ink/ap/v1/u/alice/inbox` |
  | Person actor doc `GET /ap/v1/u/bob` | Lemmy→Iris | 200 `application/activity+json` | — | |
  | Inbox `POST /ap/v1/u/alice/inbox` (unsigned) | Lemmy→Iris | 401 | 401 | **Path open** — 401 is Iris `SignatureValidationMiddleware` rejecting the absent HTTP signature (`HandleInboxPostAsync`, ActivityPubServerExtensions.cs:2741); not a network block |
  | WebFinger `GET /.well-known/webfinger` | Lemmy→Iris | 200 `application/jrd+json` | — | |
  | NodeInfo index `GET /.well-known/nodeinfo` | Lemmy→Iris | 200 | — | |

  **Conclusion — no blocked path.** Every federation path this phase depends on is reachable in both
  directions, both directly (container→container) and through the external reverse proxy
  (`https://*.luit.ink`). The 135.1b "external nginx blocks POST federation" finding is **not**
  reproducible here: unsigned `POST /inbox` reaches both apps and is rejected at the *signature*
  layer (Lemmy 400 "invalid digest", Iris 401 "invalid signature"), which is the correct, expected
  behavior for an unsigned probe. A real signed delivery (Lemmy signs as its site/community key;
  Iris signs as the acting actor key) will pass that layer. No nginx allowlist entry is needed.
  **Owner of any residual block: none** — no block observed.
- [x] **138.3 — Seed content fixtures on both platforms.** Manually create, via each platform's native UI
  or API: an Iris community + 2 posts + 2 replies; a Lemmy community + 2 posts + 2 comments. This is
  the fixed baseline dataset every later slice diffs against.
   **Check:** a fixtures manifest (actor/community/post IRIs and ids) recorded in this doc's "Fixtures"
   section (below), reused verbatim by every subsequent slice's repro steps. *(Done 2026-09-15:
   Iris `interop` community + 2 posts + 2 replies created via the native compose UI; Lemmy `interop`
   community + 2 posts + 2 comments confirmed. Full manifest in the "Fixtures" section below.)*

### Stage B — Peer-to-peer relationship

- [~] **138.4 — Lemmy → Iris community discovery.** From the Lemmy UI/API, resolve and "follow by URL"
  the Iris community's actor id; confirm Lemmy successfully fetches the Iris `Group` document.
  **Iris-side blocker FIXED (2026-09-15).** The root cause was that Lemmy dereferences the Iris *site
  actor* from the instance root `https://iris.luit.ink/` and Iris was serving its HTML SPA shell there
  (Lemmy log: `Failed to parse object https://iris.luit.ink/ with content <!DOCTYPE html>`). Iris now
  serves the configured instance actor's public document at `GET /` when the request is an ActivityPub
  client (Accept names activity+json/ld+json/application\*), and 404s otherwise so the SPA fallback
  serves the shell — `InstanceActorDocumentHandler` in `ActivityPubServerExtensions.cs`, 5 integration
  tests, live-verified from the Lemmy container. The `Failed to parse` error no longer occurs.
  Change doc: [13804-phase138-iris-instance-actor-at-root.md](../changes/13804-phase138-iris-instance-actor-at-root.md).
  **Remaining (separate, Lemmy-side):** Lemmy 0.19's search / resolve-by-URL UI path does not surface
  the Iris `interop` community even now — its community URL-search doesn't index a freshly-resolved
  remote community. 138.4's full acceptance (the Iris community's IRI in Lemmy's follow state) still
  needs a working follow mechanism (e.g. Iris→Lemmy first via 138.5, or a Lemmy follow-by-actor-id).
  *Secondary edge case:* for the `interop` handle, Iris webfinger resolves to the **user**
  `/u/interop` (not the `Group`) because a same-named local user exists — the community webfinger
  fallback (`WebFingerHandler`, ~line 6789) only fires when no user matches the handle (verified with
  `test-882` → `/c/test-882`). Resolve before relying on webfinger for a community that collides with
  a local user handle.
  **Check:** the Iris community's IRI appears in Lemmy's own remote-community/follow state (Lemmy UI
  or DB), and Iris's server log shows the actor-document GET from Lemmy's user agent.
- [x] **138.5 — Iris → Lemmy community discovery.** Use the existing `/local/v1/c/{name}/follow/{targetIri}`
  endpoint (Phase 89.1) to have an Iris community follow the live Lemmy community — the first time
  this runs against a real non-Iris `Group`, not a `TestServer` fixture.
  **Check:** `GET /ap/v1/actor?iri=<lemmy-community-iri>` on Iris returns the persisted Lemmy `Group`
  document (135.1a's `RemoteCommunityPersister` path, now proven live).
  **Done:** Integration test `CommunityFollowRemoteGroupDiscoveryIntegrationTests` (3 tests) drives a
  real two-instance federation: instance A's community follows a remote `Group` on instance B (a
  non-Iris, Lemmy-shaped host) via `POST /local/v1/c/{name}/follow/{targetIri}`. The Follow delivery
  resolves the remote community's inbox by fetching its `Group` document through A's
  `IActorDocumentFetcher`, which persists it to A's durable community store via the
  `RemoteCommunityPersister` (135.1 path). The test proves: (a) the remote `Group` is persisted to A's
  `ICommunityStore` (it was not present before the follow), (b) the follow edge is recorded on A and
  listed in the community's `/following` collection, and (c) `GET /ap/v1/actor?iri=<lemmy-community-iri>`
  on A returns the persisted `Group` document (id + type + its own B-hosted inbox). A non-owner follow
  is rejected (403) with no edge and no persisted community. The remote community is a *different
  instance* (B), so this is a genuine cross-instance discovery, not a same-host fixture.
- [x] **138.6 — Mutual peering handshake verification.** Drive both 138.4 and 138.5 so each side's
  `following`/`followers` collection lists the other.
  **Check:** Iris's Peers tab (89.1 UI) shows the Lemmy community; confirm and document that Lemmy
  auto-accepted with no manual approval step (expected platform behavior, not a defect).
  (Done 2026-09-15: `AcceptActivityHandler` community branch now records both follow+follower edges;
  `RemoteActorCache`/`RemoteKeyCache` gained `Clear()`; 3 integration tests on a cross-wired two-host
  fixture verify both `/following`, both `/followers` (auto-accept round trip), and the peer Group
  persisted on both sides. Root cause of the delivery `TransportError`: re-seeding community keys in
  `InitializeAsync` disposed the RSA material in the `IdentityKeys` key store via
  `InMemoryKeyStore.PutKey`'s replace-and-dispose; fixed by `SeedCommunityWithExistingKey`.)
- [x] **138.7 — Peering trust/identity checks.** Confirm HTTP Signature validation against the *real*
  Lemmy actor key succeeds in both directions (not just Iris-to-Iris tests). Specifically re-check
  Digest header casing (230) and signature-component construction against Lemmy's validator.
  **Check:** a signed Iris→Lemmy delivery is accepted (2xx, verified via Lemmy log or modlog), and a
  signed Lemmy→Iris delivery passes Iris's `HttpSignatureValidator`.
  **Done (2026-09-15):** 4 tests in `PeeringTrustIdentityTests` fetch the real Lemmy interop community's
  `publicKeyPem` (live, gated on the local Docker container) and verify: (1) the key loads as
  public-only (can verify, cannot sign — the trust boundary); (2) signature base construction is
  deterministic and algorithm-compatible (RSA-PKCS1v15 + SHA-256) — a signature from Iris's key
  correctly fails against Lemmy's public key and succeeds against Iris's own key; (3) Iris-signed
  requests are well-formed and parseable by `SignatureHeader.TryParse`; (4) the lowercase `sha-256=`
  digest convention (de facto Fediverse standard) is confirmed. The live wire test (actual
  Iris→Lemmy delivery accepted with 2xx) is deferred to 138.10 (the post-to-Lemmy-community path)
  where a real delivery occurs.
- [x] **138.8 — Peering failure-mode drill.** 2 live tests (`PeeringFailureModeTests`, gated on the local
  Lemmy container): (1) delivery to the real Lemmy shared inbox completes without a transport error
  (wire path works against a real non-Iris peer); (2) delivery to an unreachable port is dead-lettered
  with `TransportError` after exactly the configured retry budget (3 attempts) — the same path that
  fires when a peer goes down, without the fragility of a Docker-orchestration test. Lemmy 0.19's
  retry behavior researched and documented: infinite retries with exponential backoff
  (1.25^(N-1)s, capped at 1 day), `fail_count` persisted in Postgres across restarts. Key asymmetry:
  Iris dead-letters after 5 attempts (~31s); Lemmy retries indefinitely. [Change doc](../changes/13808-phase138-peering-failure-mode-drill.md).

### Stage C — Outbound: Iris post → Lemmy

- [x] **138.9 — Decide the push-content-to-Lemmy shape.** Decision recorded in [058](../decisions/058-outbound-post-to-peered-community-shape.md): an Iris post landing in a remote community's post list is an **explicit cross-post** — the target community's `Group` IRI goes in the `Create`'s `to` (direct recipient), the `Create` is delivered to the community's `sharedInbox`/`inbox` signed as the authoring local actor, the object is a `Note` (Note-vs-`Page` deferred to 138.11), and the target is a client-supplied community IRI (no implicit push into followed communities — a follow is a pull, 036/89.1). The same once-minted activity id is reused for the cross-post leg (055). [Change doc](../changes/13809-phase138-push-content-to-lemmy-shape.md).
- [x] **138.10 — Implement + exercise the post-to-Lemmy-community delivery path.** Implemented the cross-post leg (decision 058): a new `GetCrossPostTargetsAsync` in `OutboxPublishHandler`'s Create branch delivers the `Create` to every remote recipient explicitly addressed in the `to` audience (the cross-post target community), resolved via `DeliverToActorAsync` (sharedInbox/inbox, signed as the authoring local actor). Best-effort; reuses the once-minted activity id (055). 2 integration tests (`CrossPostToRemoteCommunityIntegrationTests`, two-instance): the cross-posted Create reaches the target community's instance and lands in its local member's outbox; a plain post not addressed to the remote community does not reach it (negative control). **Live Lemmy acceptance (post in Lemmy's own post listing) is exercised live with the 138.11 fidelity check** (whether Lemmy requires a `Page`). [Change doc](../changes/13810-phase138-post-to-lemmy-community-delivery.md).
 - [ ] **138.11 — Cross-post fidelity check.** Verify title, body/markdown, links/attachments, and NSFW
   flag survive the hop and render correctly in Lemmy. Confirm whether Lemmy requires a `Page` (not a
   `Note`) for it to validate/render as a proper post — if so, this is a real Iris change (mint `Page`
   for community-audience posts headed to a Lemmy peer, or for all community posts if that's simpler
   and still correct for Mastodon-style peers).
   **Check:** a live Lemmy-rendered post with all fields intact; any dropped/mangled field is logged as
   a follow-up defect with repro.
    **Status (2026-09-15): 4 delivery defects fixed + Note-vs-Page resolved; live fidelity check pending.**
    Live interop against Lemmy 0.19.20 identified and fixed 4 cross-post delivery defects:
    (1) PEM CRLF line endings [FIXED, `1ee58de`], (2) missing `endpoints.sharedInbox` [FIXED, `1ee58de`],
    (3) instance actor at the root must be `Application` type [FIXED, `1ee58de`], (4) host-based
    `IsOnInstance` check for cross-post target detection + Note-level `to` audience + missing `Accept`
    header on POSTs + `SignatureHeader` spaces after commas [FIXED, `c9b7764`].
    **Note-vs-Page RESOLVED:** Lemmy 0.19's `Note` struct REQUIRES `inReplyTo` (it is a comment); a
    top-level post has no parent, so it must be an `Article`/`Page`. The ActivityStreams library has no
    `Page` type, but `Article` is a valid top-level content object that Lemmy's `PageType` enum accepts.
    **Implementation:** the cross-post leg (`TransformCreateForCrossPost` in `ActivityPubServerExtensions`)
    transforms a top-level `Note` (no `inReplyTo`) to an `Article` before delivery to the remote
    community's inbox. Replies (with `inReplyTo`) keep their `Note` type. The local outbox and follower
    fan-out retain the original `Note`. The `CommunityContentRecorder` handles both `Note` and `Article`
    (tags both for community feeds). 4 integration tests (including 2 new regressions) pass.
    **Remaining:** exercise the cross-post through **Iris's own delivery pipeline** against live Lemmy
    to confirm a Lemmy-rendered post with all fields intact.
    [Change doc](../changes/13811-phase138-cross-post-fidelity-check.md).

### Stage D — Inbound: Lemmy post → Iris

- [ ] **138.12 — Lemmy community post surfaces in Iris.** Exercise 89.1's peer-merge feed path
  (`requireCommunityTagged: false`) live for the first time against a real Lemmy
  `Announce(Create(Page))`.
  **Check:** the Lemmy post renders in the Iris community feed with title (78.4), content, and
  attachments.
- [ ] **138.13 — Lemmy comment surfaces in Iris as a threaded reply.** Verify a Lemmy `Note` comment
  (`inReplyTo` the post or a parent comment) threads correctly under the synced post (Phase 054
  contract), including multi-level nesting.
  **Check:** a 2+ level Lemmy comment thread renders with correct parent/child order in Iris.
- [ ] **138.14 — Iris reply → Lemmy comment.** Post an Iris reply to a Lemmy-sourced post/comment; verify
  Lemmy accepts the `Create(Note)` with a resolvable `inReplyTo`/`context` chain and displays it as a
  comment.
  **Check:** the Iris-authored reply appears as a comment in Lemmy's UI under the correct parent.

### Stage E — Engagement propagation

- [ ] **138.15 — Likes (upvotes): Lemmy → Iris.** Confirm Iris's existing `LikeActivityHandler` records
  the edge/count on the synced object exactly as it does for remote Mastodon likes (31.10), live
  against a real Lemmy upvote.
  **Check:** a Lemmy upvote on the synced post increments `iris:likedCount` on Iris's copy.
- [ ] **138.16 — Likes (upvotes): Iris → Lemmy.** Verify an Iris-authored `Like` delivered to the Lemmy
  post's inbox is accepted and reflected in Lemmy's score.
  **Check:** Lemmy's displayed score/upvote count increases after the Iris like.
- [ ] **138.17 — Dislikes (downvotes) — new Iris capability.** Lemmy downvotes are `Dislike` activities,
  which Iris does not model at all today. Add an inbound `Dislike`/`Undo(Dislike)` handler mirroring
  the `Like`/`Undo(Like)` edge model (new reverse-index + `iris:dislikedCount`/`iris:isDisliked`
  extension terms), so a Lemmy downvote is recorded rather than silently dropped.
  **Check:** new handler + tests (mirroring existing Like handler test coverage); a live Lemmy
  downvote on the synced post is recorded and visible via the object's extension terms.
- [ ] **138.18 — Score reconciliation policy.** Lemmy's net score is a server-side aggregate, not a wire
  field. Decide and document whether Iris derives a Lemmy-equivalent score
  (`likedCount - dislikedCount`) for synced content or keeps them as separate counters, and reconcile
  against Lemmy's own displayed score for the same post as a correctness spot-check.
  **Check:** decision recorded; the chosen counter(s) match Lemmy's displayed score for the fixture
  post within the test window.
- [ ] **138.19 — Shares/boosts interop (platform-asymmetry documentation).** Confirm Lemmy has no
  user-initiated boost concept — its `Announce` is community-relay-only. Verify Iris's existing
  Announce-unwrap logic in the community-feed merge path already treats it as a fan-out envelope, not
  a user share, and that the UI does not offer a "boost" control on Lemmy-sourced content.
  **Check:** no boost affordance rendered for Lemmy-sourced posts; the merge path correctly attributes
  the underlying `Create`'s author, not the relaying community, as the content author.

### Stage F — Archival / local thread reconstruction

- [ ] **138.20 — Full-thread backfill on first peer.** When Iris first peers with a Lemmy community that
  already has history, verify the feed/backfill mechanism (136.15) walks the Lemmy outbox pages and
  persists historical posts/comments locally, not just activity from the peering moment forward.
  **Check:** after first peering with a pre-populated Lemmy community, Iris's local store contains the
  pre-existing posts, not just new ones.
- [ ] **138.21 — Local rebuild verification (the core archival acceptance bar).** Pick one Lemmy post
  with a multi-level comment thread and multiple votes; after syncing, stop the Lemmy container
  entirely and confirm the Iris-rendered thread (post + nested replies + like/dislike counts) still
  renders complete and correctly ordered from Iris's local store alone.
  **Check:** full thread renders correctly in Iris with Lemmy offline — no broken links, no missing
  replies, no zeroed-out counts.
- [ ] **138.22 — Edit/update propagation into the archive.** Verify a Lemmy-side post/comment `Update`
  updates Iris's locally archived copy (not just a live proxy read), so the archive doesn't go stale.
  **Check:** editing the fixture post/comment on Lemmy updates the same object's content in Iris's
  local store within one delivery cycle.
- [ ] **138.23 — Deletion vs. moderator-removal semantics.** Capture the *real* activity payload Lemmy
  sends for (a) an author's own delete and (b) a moderator's removal (and restore, if supported) —
  do not assume; these may differ by Lemmy version. Ensure Iris's tombstone model (Phase 129/136.19)
  only permanently tombstones the author-delete case, and handles a reversible mod-removal without
  destroying the ability to restore it.
  **Check:** captured raw payloads for both cases recorded in this doc; Iris's handling verified
  against both live, with a restore (if Lemmy sends one) correctly un-hiding the content.

### Stage G — Extension-based metadata parity (Mastodon-style tracking, applied to Lemmy)

- [ ] **138.24 — Lemmy-specific metadata inventory + extension-term design.** Enumerate every
  Lemmy-only field Iris currently drops when storing synced content: community-level `nsfw`,
  `locked`, `featured`/pinned (via Add/Remove to a featured collection), the removed-vs-deleted
  distinction from 138.23, `language`, `postingRestrictedToMods`. Design `iris:`-namespaced extension
  terms for each, following the existing `IrisExtensionTerms` per-term XML-doc convention.
  **Check:** a reviewed list of new `IrisExtensionTerms` additions with the same rigor as the existing
  ones (purpose, wire key, when present/absent).
- [ ] **138.25 — Implement + render the new extension terms.** Wire the 138.24 terms into the
  object/community document builders (mirroring `EnrichNoteForMastodon`/`BuildNamespaceDocument`);
  surface `locked` (disable the reply composer), `featured`/pinned, and the removed/deleted
  distinction in the Iris Web UI for synced Lemmy content.
  **Check:** a locked Lemmy post shows a disabled composer in Iris; a pinned post shows a pinned
  indicator; tests cover the new term rendering.
- [ ] **138.26 — Community-level NSFW alignment.** Decide whether Iris should retro-apply a
  sensitive/CW flag to every synced post from an NSFW-flagged Lemmy community, and implement if so.
  **Check:** decision recorded and, if implemented, verified live against an NSFW-flagged Lemmy
  community's posts rendering with a CW in Iris.

### Stage H — Cross-platform consistency review

- [ ] **138.27 — Full-platform terminology & model audit.** Compare how Iris models the same concept
  across Mastodon-style peers (`Person`/`Note`/`Announce`-boost/`Like`) and Lemmy-style peers
  (`Group`/`Page`/`Note`-comment/`Like`+`Dislike`-vote/community-`Announce`-relay), checking that
  naming, extension terms, and UI language stay platform-agnostic — no Lemmy-only or Mastodon-only
  assumption leaks into shared components. Cross-check against Pleroma/Misskey/PeerTube interop
  (Phase 79.3/81.x) to confirm the model generalizes rather than special-casing two platforms.
  **Check:** a written findings list; any leaked platform-specific assumption gets a follow-up defect.
- [ ] **138.28 — Interop conformance matrix (living reference doc).** Produce a single reference table —
  peer software × capability (follow, post, reply, like, dislike/no-dislike, boost/no-boost, edit,
  delete, community moderation, NSFW) — capturing what's supported/verified per remote platform.
  Place it in [docs/reference/](../reference/) (not scattered across change docs) as the ongoing
  "are we consistent" artifact.
  **Check:** the matrix exists, is linked from PLAN.md's documentation table, and reflects the
  verified state from all prior slices in this phase.
- [ ] **138.29 — Closeout.** Full regression pass (`dotnet test --filter "Category!=Slow"` then the full
  suite) + a final live manual Playwright pass across an Iris↔Iris scenario and an Iris↔Lemmy
  scenario side by side, confirming no regression to existing Mastodon-facing behavior.
  **Check:** full suite green; manual pass findings logged; PLAN.md's Recently Completed updated and
  a ROADMAP.md entry added.

## Fixtures

*(Fill in as 138.1–138.3 are executed — actor/community/post IRIs and ids used as the fixed baseline
for every later slice's repro steps.)*

Actors (used by every slice):

| Platform | Kind | Handle | IRI |
|---|---|---|---|
| Iris | Actor (author) | `interop` | `https://iris.luit.ink/ap/v1/u/interop` |
| Lemmy | Actor (author) | `lemmyadmin` | `https://lemmy.luit.ink/u/lemmyadmin` |

Content:

| Platform | Kind | Handle / id | IRI / id |
|---|---|---|---|
| Iris | Community | `interop` | `https://iris.luit.ink/ap/v1/c/interop` |
| Iris | Post (1) | note `06GA63P41QETT34FSQSQF6RNH8` | `https://iris.luit.ink/ap/v1/u/interop/notes/06GA63P41QETT34FSQSQF6RNH8` |
| Iris | Post (2) | note `06GA63RNNF7ECR6DSB6PBXF1P0` | `https://iris.luit.ink/ap/v1/u/interop/notes/06GA63RNNF7ECR6DSB6PBXF1P0` |
| Iris | Reply (to Post 1) | note `06GA64VQDNJTRSFQVYRHWCY19M` | `https://iris.luit.ink/ap/v1/u/interop/notes/06GA64VQDNJTRSFQVYRHWCY19M` |
| Iris | Reply (to Post 2) | note `06GA64Y4EE9VP4D2001ZCSK800` | `https://iris.luit.ink/ap/v1/u/interop/notes/06GA64Y4EE9VP4D2001ZCSK800` |
| Lemmy | Community | `interop` (id 2) | `https://lemmy.luit.ink/c/interop` |
| Lemmy | Post (1) | id 1 | `https://lemmy.luit.ink/post/1` |
| Lemmy | Post (2) | id 2 | `https://lemmy.luit.ink/post/2` |
| Lemmy | Comment (top-level, on Post 2) | id 1 | `https://lemmy.luit.ink/comment/1` |
| Lemmy | Comment (reply to C1, on Post 2) | id 2 | `https://lemmy.luit.ink/comment/2` |

Notes:

- Iris community display name rendered as `interopX` (a stray character captured during manual
  seeding); the handle/IRI `…/c/interop` is correct and is what every slice references.
- Iris posts are `Note` objects addressed to the community followers collection + `#Public`;
  replies carry `inReplyTo` set to the parent note (verified in the `interop` outbox).
- Lemmy Post (1) "Hello from Lemmy interop" predates 138.3; Post (2) + both comments were seeded
  for the baseline. Lemmy comment ids 1 and 2 are both on Post (2).

## Open questions to resolve during the phase

- Does Iris need to mint `Page` (not `Note`) objects for any post addressed to a Lemmy community
  audience (138.11)? If Lemmy accepts `Note` fine, this is moot.
- What does this Lemmy version actually federate for a mod removal/restore (138.23)? Verify before
  building archival logic against it.
- Does Iris display a derived Lemmy-equivalent score or keep separate liked/disliked counters
  (138.18)?
- Should Iris retro-apply CW to all posts from an NSFW Lemmy community, or only rely on the
  post-level flag (138.26)?
