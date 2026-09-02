# Iris — Roadmap

Working roadmap: where we've been, where we are, what's left. Per-slice detail lives in [changes/](changes/README.md), [decisions/](decisions/README.md), and [phase-notes/](phase-notes/README.md) — this file stays one line per waypoint.

## Where we've been

| Phase | Status | One line |
|---|---|---|
| -1 — Project Reorganization | ✅ | Domain folders + nested namespaces, mirrored across `src` and `tests`. |
| 0 — Scaffolding | ✅ | Solution, net10.0 projects, central package management, multi-instance `TestServer` harness. |
| 1 — Core | ✅ | `Iri`, identity/keys, HTTP signatures, caching. |
| 2 — Client | ✅ | Signing pipeline, WebFinger, paged collections, retry + content-negotiation handlers. |
| 3 — Server Foundation | ✅ | Persistence seam + in-memory impl, `/ap/v1` endpoints, server caching. |
| 4 — Inbox & Delivery | ✅ | Inbound validation, activity handlers, delivery queue/worker, Follow lifecycle. |
| 5 — Community / Groups | ✅ | Community store + endpoints, unified feed, `iris:capabilities`. |
| 6 — Proxy Fallback | ✅ | Server proxy endpoint + policy stack, client `ProxyFallbackHandler`. |
| 7 — Samples & Blazor | ✅ | `Iris.Client.Extensions`, `SampleServer`, `SampleBlazorClient`, E2E tests. |
| 8 — Sample Docker Composition | ✅ | Multi-instance compose + WASM server-explorer (S1–S11, [DEPLOYMENT](reference/DEPLOYMENT.md)). |
| 9 — Deployment Preparation | ✅ | FQDN/TLS plan, enumeration + compat + risk registers (prep only). |
| 10 — Project & Test Review | ✅ | Suite audit + consolidation (850 → 832 tests). |
| 11 — Usability / Gap Closure | ✅ | User journeys, discovery/follow/post, failure-mode + operator-reject coverage. |
| 12 — Spec Conformance | ✅ | F-01…F-31 gap closure (F-26/F-31 deferred) + conformance suite. |
| 13 — Live Federation Compatibility | ✅ | CI-testable slices done (13.1–13.4, 13.8; closed the F-26/F-28 deferrals via opaque passthrough); the live interop scenarios (13.5–13.7, 13.9–13.10) are now executed as **Phase 19.1** against the public FQDNs (the FQDNs are live: `iris-dev1.luit.ink` / `iris-dev2.luit.ink`). |
| 14 — Live-Interop Execution & Remediation | ✅ | Absorbed into Phase 19.1 (execution) + 19.4 (remediation); the deferred Mastodon live test runs in 19.2. |
| 15 — Auth Upgrade | ✅ | Bearer validator, full OAuth2 (token, refresh, authorize + WASM browser flow), samples/docs. |
| 16 — Persistence & Scaling | ✅ | Bounded delivery concurrency, file-backed queue + dead-letter, per-peer rate limiting, file-backed persistence (all opt-in). |
| 17 — Observability & Transport Hardening | ✅ | Health checks + graceful shutdown, delivery metrics, circuit breaker + retry hardening, inbound rate limiting. |
| 18 — Client/Server Hardening | ✅ | 18.1–18.3 done (`Retry-After` HTTP-date client+server, e2e 429→retry); no further slices defined. |
| Sample Explorer — 2nd & 3rd rounds | ✅ | Library-coverage gaps closed (relays, home timeline, deep-linking, unlike, delete, …); live-browser acceptance verified (change [123](changes/123-sample-explorer-live-browser-acceptance.md)). |

## Where we are — Phase 19: production-style manual integration testing

> The FQDNs are live (`.env`: `iris-dev1.luit.ink` → `iris-a`, `iris-dev2.luit.ink` → `iris-b`, both
> reverse-proxied over TLS; `IRIS_CORS_ORIGINS` includes the public origins) and a real public Mastodon
> account exists for testing: **`@RayvenMX@mastodon.world`**. This phase is a **manual, live,
> production-style integration-testing program**: we test the live system **as a user, driving the
> sample UI with Playwright MCP browser tools** (no Playwright test framework — this is manual
> evaluation of the running stack, one slice per loop turn). The compose stack + public FQDNs are the
> entire evaluation environment (no additional infrastructure).
>
> **Method (binding for every evaluation slice):**
>
> 1. **Reproducible environment.** Every turn starts from a clean `docker compose down` +
>    `up --build -d` (after 19.0, with volumes), waits for health, and uses the public FQDNs
>    (`https://iris-dev1.luit.ink`, `https://iris-dev2.luit.ink`, UI on the published port) — exactly
>    what a production deployment looks like.
> 2. **Act like a user.** Drive the explorer UI with the Playwright MCP tools (navigate, click, type,
>    read the rendered DOM, capture console/network evidence). Do not test via raw HTTP where a UI
>    path exists — raw HTTP (curl) is only for verifying wire-level facts the browser cannot see
>    (outbox JSON, signature headers, delivery to the peer).
> 3. **Verify on the wire, not just in the UI.** A UI action is only *verified* when its server-side
>    effect is confirmed: the activity appears in the correct outbox/collection, the peer instance
>    received + validated + recorded it, and no unintended activity was generated (no loops/echoes).
> 4. **Record evidence.** Each slice writes its findings to a change doc (`docs/changes/`) as a
>    checkpoint table (waypoint → expected → actual → PASS/FAIL + evidence). Findings are one of:
>    **PASS**, **FAIL** (bug — becomes a remediation item in 19.4), **GAP** (predicted gap confirmed),
>    **BLOCKED** (external dependency — e.g. Threads refuses; note and move on).
> 5. **Small, checkpointed slices.** Each loop turn completes one waypoint (or a few tightly related
>    ones), commits it, and ends. A FAIL is recorded and deferred to 19.4 — the evaluation phase never
>    fixes bugs it finds (except trivial environment fixes that block the next turn).
> 6. **Be polite to the public internet.** Low request volume, no spammy content, respect rate limits,
>    use only the sample account + the seeded sample actors, and never post anything we would not want
>    to be public.

> **Tabled (operator decision):** external/remote *community-style* interaction testing (e.g. our
> community joining a remote community's social graph, remote community management) has no plan yet and
> is deferred. Local community management (creation, membership, following, feeds) **is** in scope (19.5).

### Phase 19.0 — Evaluation environment (prepare)

Prepare the live stack so every later phase tests a durable, production-shaped environment.

- [x] **19.0.1 — Volumes for server state.** Add named Docker volumes (or bind mounts) for
  `iris-a`/`iris-b` state in `docker-compose.yml`; switch the sample server to opt-in
  `UseFileBackedPersistence` (Phase 16.4) behind an env var (default stays in-memory), with the volume
  as the persistence directory; the delivery queue (Phase 16.2) uses the same volume. Verify:
  `docker compose down` (no `-v`) → `up` → actors, keys, follows, outbox content, and pending delivery
  all survive; a fresh `down -v` + `up` resets cleanly.
- [x] **19.0.2 — Seed determinism + idempotency.** Seeding must be safe to run against a non-empty
  volume (idempotent by IRI, never duplicates actors/notes across recreations) and must not clobber
  state created during testing (e.g. a follow made in a prior turn survives a recreation).
- [x] **19.0.3 — FQDN + TLS + CORS audit.** Verify end-to-end over the public FQDNs: WebFinger on both
  instances, the UI origin in `IRIS_CORS_ORIGINS` matches the UI's actual origin, advertised IRIs are
  clean `https://iris-devN.luit.ink/...` (no port), and the peer instances' `Iris__PeerBase` resolves
  each other's *public* IRIs (not just the in-network names) so federation works after a volume-backed
  recreation. Fix whatever is miswired; smoke via `scripts/docker-smoke-test.sh`.
- [x] **19.0.4 — Test-account readiness.** Confirm `@RayvenMX@mastodon.world` is resolvable via
  WebFinger from our instances, its actor document fetches + key validates, and our sample actors'
  Basic-auth logon works from the public UI origin. Record the account's capabilities (posting,
  follows) as the known-good external reference.
- [x] **19.0.5 — Evaluation checklist scaffold.** Create `docs/reference/LIVE_EVALUATION_CHECKLIST.md`
  (or extend the INTEROP_TEST_HARNESS §4a checklist) as the standing manual checklist the Playwright
  sessions execute: the standing checklist (logon, explore, switch instance, cross-instance write,
  moderate, external instance) + every Phase 19 waypoint mapped to a UI path. This is the operator's
  (and the agent's) repeatable routine between turns.

**Definition of done for 19.0:** stack recreated twice with `down`/`up` (no `-v`) with zero data loss;
smoke test green over the FQDNs; the checklist doc committed.

### Phase 19.0b — AP-native rework (architectural pivot, **gates 19.1+ execution**)

> **Status: IN PROGRESS.** Plan: `docs/changes/161-ap-native-rework-plan.md`.
> **Governing principle (operator directive):** the *general flow* is AP-native (everything flows
> through the actor's **outbox**); *specialized* capabilities (proxy relay, search, feeds) are backed
> by an `iris:`-namespaced JSON-LD extension (`iris:capabilities`) for discovery but keep their own
> transport — never invented as AP activity types. Mute/relay are local, non-federated, **not** AP
> activities (D4a). **This changes the follow Accept/Reject mechanism that 19.1.2 (F1) exercises, so it
> must land before live follow testing resumes.**

- [x] **19.0b.1 — Outbox Accept/Reject.** `OutboxPublishHandler` + `CommunityOutboxPublishHandler` gain
  `Accept` + `Reject` branches (Accept ensures the follower→actor edge + delivers to the follower's
  inbox; Reject removes the provisional edge + delivers). Deterministic IRIs `{actor}/accepts/{follow}` /
  `{actor}/rejects/{follow}` preserved. New AP-core client `AcceptAsync`/`RejectAsync` post to the
  outbox. (change 161a: 6+3 tests, suite 1,269.)
- [x] **19.0b.2a — Remove the follow-decision endpoints.** Delete the person + community
  `follows/{**followId}` POST endpoints + `LocalFollowDecisionHandler` +
  `CommunityFollowDecisionHandler` (+ the shared `HandleFollowDecisionCoreAsync`); the outbox path is the
  sole accept/reject write. Sample UI "Inbound follows" card flipped to `AcceptAsync`/`RejectAsync`.
  Retired the 24 endpoint tests; repointed `OutboxSingleSourceOfTruth` + `FederationSignature` to the
  outbox. (change 161b: suite 1,245.) `remaining:` none for follow decisions.
- [x] **19.0b.2b — Local-moderation transport (D4a).** Mute/relay are **specialized local
  capabilities** (not general flow), so they are **kept** (not removed) — relocated off the
  `/ap/v1` AP tree onto a dedicated non-AP `/local/v1` tree so the `/ap/v1` POST surface becomes
  outbox-only. `LocalMuteHandler`/`LocalRelayHandler`/`CommunityMuteHandler` move to a separate
  `MapGroup('/local/v1')` (still Basic-auth, non-AP); the reads stay on `/ap/v1`. `mute`/`relay`
  `iris:capabilities` values added (person: `mute`/`relay`; community: `mute`). Client's
  `LocalModerationClient` now derives the local URL from the actor IRI (host + `/u/`/`/c/` segment)
  under `/local/v1` (new `LocalModerationConstants`). (change 161e: suite 1,254.)
- [x] **19.0b.3 — Split the client.** Move `MuteAsync`/`UnmuteAsync`/`SubscribeRelayAsync`/
  `UnsubscribeRelayAsync` off the core `IActivityPubClient` into a new `LocalModerationClient` (the
  `AcceptFollowAsync`/`RejectFollowAsync` methods were already **removed** in step 1 — change 161c, they
  targeted the deleted follow-decision endpoints). Done in two steps: **step 1** (change 161c) removed the
  dead follow-decision client methods; **step 2** (change 161d) created `ILocalModerationClient` +
  `LocalModerationClient` (local, Basic-auth, non-AP), added
  `IActivityPubClientFactory.CreateLocalModerationClient` + the extension/DI chain
  (`IrisClientFactory` → `IrisClientBundle` → `ClientService.GetLocalModerationClient()` →
  `ExplorerSession.GetLocalModerationClient()`), flipped the sample UI's Moderation/relay cards to the new
  client, dropped the 4 mute/relay methods (+ the `_localAuth` constructor overloads) from
  `IActivityPubClient`/`ActivityPubClient`, repointed the collection-read tests + screen tests to the new
  client, and added 7 unit tests. (change 161d: suite 1,252.) `remaining:` the stale "InboxOf" doc comments
  (the code already posts to the outbox) — folded into 19.0b.4.
- [x] **19.0b.4 — Decisions + docs.** D1–D4 **resolved** (change to the plan §4: keep proxy/search/
  feeds as `iris:`-extension-discovered capabilities; mute/relay = D4a local, non-AP). `done:`
  swept COMPATIBILITY_MATRIX / LIVE_EVALUATION_CHECKLIST / LIVE_INTEROP_TEST_PLAN for the removed
  endpoints; fixed stale `InboxOf` doc comments in `IActivityPubClient` (every typed method now
  publishes to `actorId.OutboxOf()`); renamed `DeliverAsync` param `inboxId` → `targetId`;
  full build + full test suite green (1,254 tests, 0 failed). (change 161f.)

**Definition of done for 19.0b:** no non-AP POST routes under `/ap/v1` except the outbox (and any
explicitly-kept borderline ones per D1–D3); the client is a pure AP protocol layer; the full suite is
green; the plan's decisions are recorded.

### Phase 19.1 — Live interop verification (was Phase 13.5–13.10 + 14 execution)

Execute the [compatibility matrix](reference/COMPATIBILITY_MATRIX.md) scenarios against the live
network, through the UI where a UI path exists and over the wire for server-side behavior. The
predicted gaps (§5 of the matrix) are re-checked against the *current* code (several were since closed
by later phases — outbound `Create`, `Undo`, group-follow, global search, EdDSA, person-inbox `Create`
handling exist now — so expectations must be re-derived from source before each scenario).

- [x] **19.1.1 — Iris↔Iris baseline.** alice@iris-dev1 ↔ alice@iris-dev2: follow (UI), Accept round-trip
  (wire: both outboxes), unfollow via `Undo` (edge removed on both sides), like, post+reply (peer's
  inbox received the `Create`), community follow, community post surfacing on the peer. This is the
  "sanity check before external platforms" baseline. F-1911-1 (Undo followers-edge) and F-1911-2
  (outbox 20x duplication) **fixed** (change 140, commit 262fd09); F-1911-3 (community follow delivery
  loss) → 19.4. Community post surfacing not tested (dependent on F-1911-3 fix).
- [ ] **19.1.2 — Follow scenarios (F1–F4)** against `@RayvenMX@mastodon.world`: they follow us → we
  `Accept` (we publish a deterministic `Accept` to our outbox — AP-native, Phase 19.0b; wire: their inbox;
  UI: our followers collection); we follow them (UI) → their `Accept` arrives and is recorded; `Reject`
  behavior (we publish a deterministic `Reject` to our outbox → does the peer see a `Reject`? the legacy
  follow-decision endpoint is removed); unfollow via `Undo` (does Mastodon remove the relationship? check
  their profile UI).
   `remaining:` F2 (we follow them) **PASS (signature)** — the F-1912-1 signature fix (SHA-256 digest,
   no trailing newline) made Mastodon accept our Follow (202); F-1911-3 (community signing identity not
   registered) **fixed** (server + client) and verified (in-process regression test + community-signed
   follow to Mastodon 202 via IrisSigner). RayvenMX's `Accept` still pending (their side to process).
   F1/F3/F4 not tested (require RayvenMX's action). **Live state (change 161-preface):** the named
   volumes were reset (`down -v`) before this live pass, so the prior F2 `Accept` + any earlier F1
   follow are not on the current volume — F1/F2 are re-driven on the fresh volume per
   `docs/reference/LIVE_INTEROP_TEST_PLAN.md` (operator triggers F1: RayvenMX follows
   `alice@iris-dev1.luit.ink`; agent records + Accepts + verifies delivery). Persistence across
   `down`/`up` (no `-v`) was **confirmed working** (named volumes + file-backed stores round-trip). → 19.4.
- [ ] **19.1.3 — Post/receive scenarios (C1–C4).** We post a Note (UI compose) → signed `Create`
  delivered to RayvenMX's inbox → **Mastodon renders it** (check the public post URL on
  mastodon.world — this is the core "post and have it federate" proof). RayvenMX posts → our inbox
  records it → visible in the relevant local feed/collection. Extended-type objects (a Mastodon
  `Video`/`Article` if one is available; otherwise a toot with `sensitive`/`spoilerText`) round-trip
  without rejection.
- [ ] **19.1.4 — Signature scenarios (SIG1–SIG5).** Inbound from Mastodon: draft-cavage-10 RSA-SHA256
  validates (their `Create`/`Follow` land without 401); Ed25519 inbound (if a test target signs with
  EdDSA) validates; unsigned POST is rejected 401; our ServerToServer profile (with `digest`) is
  accepted by Mastodon (our `Follow`/`Create` reach them); unsigned GETs flow both ways.
  > **Finding (live, ad-hoc interop):** the *browser* client's direct signed POST to its own outbox
  > 401'd (the proxy fallback masked it). Root cause: a Blazor WASM host's `fetch` treats the standard
  > `Date` request header as forbidden and overrides it on the wire, so the server verified the `date`
  > signature component over a value different from the one signed. **Fixed** by carrying the signed
  > date in a custom, non-forbidden `X-Signature-Date` header (the browser sends it faithfully); the
  > verifier reads the date component from it (falling back to the wire `Date`), and `Date` is still
  > sent for replay protection. Live-verified: direct outbox POST → 202, no proxy fallback. Change
  > `161o-browser-signed-post-401-x-signature-date.md` (commit `fbbf041`).
- [ ] **19.1.5 — Pagination (P1–P2) + content types (T1–T3).** A Mastodon client (their UI/REST API)
  pages our outbox via `?page`/`?limit`; we page through a Mastodon collection (their outbox) to
  exhaustion — note any cursor-paging mismatch. We serve `application/activity+json`; we accept
  `application/ld+json` + extended `@context` inbound (their `Create` bodies).
- [ ] **19.1.6 — Community scenarios (G1–G4) vs. a remote *actor* following our community** (G1, G3):
  RayvenMX (or another willing public actor) follows our local `iris` community → we `Accept` → they
  appear in `members`/followers (UI community screen). G2 (our community follows a remote community)
  and G4 (content from a followed remote community) are **tabled** with external community-style
  interaction — record the current behavior as an observation, do not build a plan.
- [ ] **19.1.7 — Discovery (S1–S2, nodeinfo).** Our nodeinfo + webfinger are consumable by
  mastodon.world (does their instance directory/admin surface see our instance?); our global search
  (`/ap/v1/search`) lists local actors + content; we fetch their public profile via the explorer's
  object view (read-only reconnaissance).
- [ ] **19.1.8 — Matrix re-baseline.** Update COMPATIBILITY_MATRIX.md §5 (gap summary) with the live
  outcomes: which of the six predicted gaps are now closed by later phases, which remain, which new
  mismatches the live runs surfaced. Findings → 19.4.

### Phase 19.2 — Real-world Mastodon account: @RayvenMX@mastodon.world

Deep, user-perspective verification centered on the real public account (the reference for what our
objects *should* look like and where they *should* appear).

- [ ] **19.2.1 — Inbound: their activity flows to us.** Have RayvenMX post a public toot (and, if
  possible, a reply to one of our notes) → verify our server stores the `Create`, the object is
  fetchable by IRI via the explorer's object view, and it appears in the correct local surfaces
  (community feed / member outbox / followed feed, as applicable). Verify signature + content-type
  handling on the wire.
- [ ] **19.2.2 — Outbound: our activity flows to them and renders on mastodon.world.** Follow, post,
  reply (to their toot → verify the reply shows under their post's thread), like, boost (`Announce`) —
  each verified **on mastodon.world itself** (public URLs) as the source of truth, plus wire-level
  confirmation of the signed delivery.
- [ ] **19.2.3 — Object-shape conformance.** Fetch the same object from our server and from
  mastodon.world and diff the shapes: `@context` handling, `attributedTo`/`to`/`cc` audiences,
  `content` HTML, `url`, `published`/`updated`, `inReplyTo`, `tag`, `attachment`, `sensitive`,
  `spoilerText`. Record where our output deviates from the Mastodon reference. **Decision point:**
  where we are *richer* than typical servers (e.g. embedding the full object in an activity instead of
  passing a link, richer `tag`/`url` data), confirm Mastodon still consumes it — enrichment is allowed
  **only while conformance holds**; a divergence that breaks the receiver is a FAIL for 19.4.
- [ ] **19.2.4 — Threads/replies compatibility (Mastodon baseline).** Build a 3-level thread
  (note → reply → reply-to-reply) via the UI; verify `inReplyTo` chains render correctly on
  mastodon.world (thread nesting) and that our object view renders the reply chain (conversations).
  This is the baseline for the 19.7 Threads probe.
- [ ] **19.2.5 — Delete/moderation propagation.** Delete one of our posts → `Delete` propagates to
  Mastodon (their UI shows the tombstone/graveyard); mute (local) / block + flag (federated) → verify
  which of these Mastodon honors and record the semantics (their docs say only `block`/`flag` are
  standard AP moderation; confirm empirically). Undo of like/unfollow also propagates.

### Phase 19.3 — Two-instance network: loops, echoes, convergence

The small iris-a/iris-b network must behave like a well-behaved federation: no activity-forward
loops, no echo amplification, and eventual consistency of edge state.

- [x] **19.3.1 — Follow-loop safety.** Mutual follows (alice-a ↔ alice-b) must not re-deliver
  activities back and forth: post on A → lands in B's inbox exactly once → appears in B's stores
  exactly once → B does **not** re-deliver A's post to A (no forwarding of already-local content).
  Verify by counting occurrences in outboxes/stores after each recreation and after repeated posts.
  **Resolved (19.3.1 fix, commit 262616e):** the `InboxProcessor` now stores each inbound activity
  add-if-absent by IRI (C-07) and skips re-dispatch for a re-delivery, so a peer's echo of a Create is
  not re-fan-out. `MutualFollowDeliveryLoopIntegrationTests` proves the post reaches the peer a bounded
  number of times (pre-fix the loop produced thousands of deliveries).
- [x] **19.3.2 — Echo/amplification check.** With both instances following each other (and the
  community following the peer), post once; enumerate every delivery event (delivery queue + peer
  inboxes) and assert the total is bounded (no quadratic growth, no re-announce of announces).
  Specifically: an `Announce` (boost) of a peer's post must not be re-announced by the peer (boost
  loops are the classic federation failure). **Resolved (19.3.2, commit 262616e):** the same
  inbox-Id dedup guard bounds the echo/amplification for both `Create` and the `Announce` propagation
  path (the propagated boost is re-stored under its deterministic IRI and is not re-announced).
- [x] **19.3.3 — Announce propagation.** Boost a note on A; the boost reaches B's followers once;
  boost a note *from* B on A (boosting remote content) — verify no infinite announce chain and the
  correct `object` link (not an embedded copy that could double-attribute). **Resolved (19.3.3, commit
  a0c86ad):** the propagated boost's deterministic IRI was keyed off the receiving inbox instead of the
  announcer, so the local-follower leg never matched; the `AnnounceActivityHandler` now scopes the IRI to
  the announcer and records local followers in their outbox directly (remote followers are delivered to
  their inbox signed by the announcer). `AnnouncePropagationIntegrationTests` proves a boost reaches the
  peer's local follower exactly once (bounded, no re-announce) and a remote-note boost carries the correct
  `object` link with no infinite chain.
- [x] **19.3.4 — Delete propagation, both directions.** Delete a local note → peer tombstones it;
  delete a note *originating* on the peer (if our instance can delete remote-originated content,
  e.g. a local reply to their note) → correct scope, no collateral deletion. **Resolved (19.3.4):**
  direction 1 (local delete → peer tombstone) was already covered; direction 2 (a *remote* actor
  deletes a note we hold a copy of) now has a two-instance test proving the `DeleteActivityHandler`'s
  owner guard accepts a remote author only for an attributed copy (no foreign tombstoning), tombstones
  only the referenced object (no collateral deletion), and does **not** re-propagate (only the home
  instance re-fans-out — the re-propagation branch is gated on the deleting actor being local).
  `RemoteAuthorDelete_LocalCopyTombstoned_NoCollateral_NoRePropagation` (change 144).
- [x] **19.3.5 — Follow-edge convergence.** After a follow/unfollow/re-follow cycle across the two
  instances, both sides' `following`/`followers` collections converge and agree (same IRIs, same
  counts, stable pagination) — no orphan edges, no duplicate edges. **Resolved (19.3.5):** the
  two-sided lifecycle already converged correctly — the `FollowActivityHandler` records the directed
  edge on the target's side (and the follower's own `following` set on the home instance) and the
  `UndoActivityHandler` removes the edge from *both* sides (the target's followers set when the
  recipient is the target, the follower's own following set when the recipient is the un-follower), so
  a re-follow simply re-records it (the stores are `HashSet`-backed — no duplicate IRI). No production
  change. `FollowEdgeConvergenceIntegrationTests` drives a signed Follow / Undo / Follow cycle over the
  wire and asserts both stores agree on the single edge, no orphan on either side after the un-follow,
  no duplicate on the re-follow, and the public `following`/`followers` endpoints are stable across
  re-reads (change 145).
- [x] **19.3.6 — Update propagation.** Update (re-publish with new content, same IRI) one of our
  notes → the peer's stored copy is updated (or correctly ignored if we don't implement Update
  handling — record which, and whether the object endpoint serves the new content). **Resolved
  (19.3.6):** Update handling is implemented and correct in both directions. Direction 1 (a local
  author edits a note; the peer that received it via the outbound `Create` federation is refreshed)
  was already covered by `LocalUpdate_IsFederatedToRemoteFollower_RemoteCopyRefreshed`. Direction 2
  (the inverse — a *remote* author edits a note this instance holds a copy of) was the gap and is now
  pinned: the `UpdateActivityHandler`'s owner guard accepts a remote author only for an *attributed*
  copy (no foreign rewrite), refreshes only the referenced object (no collateral rewrite), and does
  **not** re-propagate (only the home instance re-fans-out — the re-propagation branch is gated on the
  updating actor being local). `RemoteAuthorUpdate_LocalCopyRefreshed_NoCollateral_NoRePropagation`
  (change 146).
- [x] **19.3.7 — Recreation stability.** A host that has delivered an outbound federation `Create` is
  recreated (`down` (no `-v`) + `up`) and the re-created instance's delivery queue replays the
  already-delivered activity from its on-disk journal. The replay is a harmless no-op, not a
  re-delivery storm: the peer stores the activity exactly once, lists it in the recipient's outbox
  exactly once (no duplicate edge), and does not re-fan-out the activity (bounded outbound
  deliveries). **Resolved (19.3.7, change 147):** the guarantee rests on two independent guards,
  pinned end-to-end by `Recreation_DeliveredCreateReplayed_StoredOnce_NoReFanOut_OutboxUnchanged`
  with no production change. (1) The file-backed `FileBackedDeliveryQueue` journals every enqueued job
  to disk and, on construction, replays every journaled job into its channel (at-least-once); the
  default shutdown service completes the queue but does not truncate the journal, so the un-truncated
  journal re-sends the already-delivered Create on `up` (the test asserts the replay is a genuine
  re-transmission — A's worker sends it exactly twice over the wire, not a no-op that never left A).
  (2) The peer's `InboxProcessor` stores an inbound activity add-if-absent by its `Id` (C-07) and, on
  a re-delivery, does **not** re-dispatch it to a handler — so the replay is stored as a no-op and
  never re-fan-out; the outbox stays unchanged in length (no duplicate edge) and `InMemoryActivityStore
  .AddToOutboxAsync` is idempotent-by-IRI (F-1911-2).

### Phase 19.4 — Remediation

- [ ] **19.4.1 — Triage.** Collect every FAIL/GAP finding from 19.1–19.3 + 19.5–19.7 into a prioritized
  list in a change doc (each with repro steps + wire evidence).
- [ ] **19.4.2 — Fix in priority order** (federation correctness first: loops/echoes, signature
  failures, delivery loss; then conformance: object shape, audiences; then UI: navigability,
  rendering). Each fix is its own vertically-complete slice (impl + tests) per the loop rules;
  re-run the failing waypoint to confirm.
- [ ] **19.4.3 — Regression re-verification.** After the triage list is empty, re-run the full
  evaluation checklist (19.0.5) end-to-end over the FQDNs and record a clean sweep.

### Phase 19.5 — Community creation & management

How do we create and manage communities, and their peers (the communities/actors they follow)?

 - [ ] **19.5.1 — Community creation surface.** Establish (or build, if absent — note as a finding) a
   UI path to create a community (Group) via the outbox-publish pattern (a signed `Create`/`Add` of a
   Group to the community outbox, or the management-style ActivityStream message per the 19.6
   architectural rule). Verify the new community: document endpoint, `members`, empty `feed`,
   `following`/`followers`, and WebFinger/`iris:capabilities` discovery.
   `remaining:` the community READ surface is now complete and pinned (change 148): the community
   document, `members`, `feed`, `following`/`followers`, and search collections were already served;
   the missing piece was the advertised **`outbox` link** — `GET /ap/v1/c/{name}/outbox` (the READ
   counterpart of `POST /ap/v1/c/{name}/outbox`) is now a paged collection served through the
    local collection-page cache (page 1 `OrderedCollection`, page N>1 `OrderedCollectionPage`,
    `?refresh=true` bypass), so a remote client resolving the community's outbox link finds the
    community's authored activities instead of a 404. The CI-testable **creation write path is now
    complete** (change 161l): the client's one-call `CreateCommunityAsync(actorId, name, displayName)`
    builds a `Create` of a `Group` (IRI `{base}/ap/v1/c/{name}`, derived from the creator's instance)
    and publishes it to the creator's **own outbox** (the AP-native outbox-publish pattern — the
    chicken-and-egg of a community publishing to its own not-yet-existent outbox is avoided by having
    the creator's outbox carry the `Create`). The server's outbox-publish handler, on seeing a
    `Create` whose embedded object is a local `Group`, materializes the community (stores it in the
    community store, minting a signing key on first creation and reusing it on re-creation, stamping
    the `publicKey` extension) so the new community's document endpoint, `members`, `feed`, and
    collections resolve (`CommunityCreationIntegrationTests`). Still open for full 19.5.1: the UI
    creation screen (the Blazor form that calls `CreateCommunityAsync`) and the
    WebFinger/`iris:capabilities` discovery verification — both live-verification / UI items.
 - [ ] **19.5.2 — Membership management.** Add/remove members via management-style activity messages
   (not direct store writes): an actor joining (an `Add` to `members`, or a Follow-based join if that's
   the chosen model — record the decision in a decisions doc), leaving (inverse), and the community
   feed reflecting membership changes.
   `remaining:` the `Add`/`Remove` membership mechanism already existed (F-09) but had **no
   authorization** — any signature-validating actor could add/remove members of a local community. This
   slice (change 150) adds the 19.5.2 **self-management gate** to `AddActivityHandler`/
   `RemoveActivityHandler`: an `Add`/`Remove` to a community's inbox applies only when the activity's
   **actor is that community itself** (the same gate the community outbox publish endpoint applies to
   its `actor`). A community-signed `Add`/`Remove` posted through its own inbox now adds/removes the
   member, and the community **feed + `members` collection reflect the change on the wire** (new
   `CommunityMembershipManagementIntegrationTests`); a remote actor's signed `Add`/`Remove` is stored
   (signature validated) but no longer modifies the membership (`AddRemoveFederationIntegrationTests`
   updated). Decision recorded in the change doc: the community manages its own membership via
   `Add`/`Remove` through its own inbox (self-management); a remote actor's *join request* is a
   Follow/accept flow (19.5.x), not an `Add` it may post to the community inbox. Still open for full
   19.5.2: the UI membership-management screens (add/remove member from the community page) and the
   remote-actor **join request → accept** flow — both UI/live-verification items.
- [ ] **19.5.3 — Community peers (following management).** The community follows a remote actor/
   community via `POST /ap/v1/c/{name}/outbox` (Follow) — verify the edge, the `following` collection,
   and delivery to the target; unfollow via `Undo` (edge removed, peer notified); reject/undo flows
   for inbound follows of the community (we reject a follow → the peer sees `Reject`).
   - [x] **Person inbound-follow accept/reject (the `manuallyApprovesFollowers` live half, J-10 /
     Resolved Decision #46) is complete** (change 151): a single operator follow-decision endpoint
     (`POST /ap/v1/u/{handle}/follows/{**followId}`, Basic-auth, the follow resolved by IRI from the
     activity store; a trailing `/accept` selects acceptance, otherwise reject) builds + records +
     server-delivers the deterministic `Accept` (ensures the edge) or `Reject` (removes the edge), and the
     remote side finalizes/removes its edge on receipt. The client (`AcceptFollowAsync`/
     `RejectFollowAsync`), the sample UI "Inbound follows" card, and the opt-in
     `Iris__ManuallyApprovesFollowers` sample flag are in; inbound follows are surfaced in the followed
     actor's outbox so the UI can list them. Verified end-to-end over the two-instance Docker env (a
     signed inbound follow of a gated alice → operator Accept finalizes the edge on both sides; operator
     Reject removes it on both sides; unauthenticated → 401). Full suite 1,217 green.
   - [x] **Community inbound-follow accept/reject (19.5.3 "reject/undo flows for inbound follows of the
     community") is complete** (change 152): the person decision logic is extracted into a shared
     follow-decision core, and a community variant — `POST /ap/v1/c/{name}/follows/{**followId}`
     (Basic-auth, the community's IRI is the credential seam; a trailing `/accept` selects acceptance) —
     builds + records the deterministic `Accept`/`Reject` in the activity store + the community's outbox and
     ensures/removes the community's **follower edge** (`ICommunityStore` followers set). The community
     branch of `FollowActivityHandler` now surfaces the inbound follow in the community's outbox and
     applies the `manuallyApprovesFollowers` gate (a gated community records its edges but does not
     auto-accept). 12 new integration tests (accept/reject: 202 + edge, idempotent re-decision, 401, 409
     wrong target, 403 local follower — a local *community* following the community — 410 not recorded) + 2
     handler unit tests; full suite 1,231 green. (The community **UI** "Inbound follows" card + the
     cross-instance wire drive are the remaining live/UI items for full 19.5.3; the delivery path is the
     same one the person path proved in the two-instance Docker env.)
   - `remaining:` for full 19.5.3 — the community *outbound* follow/unfollow is already done
     (`POST /ap/v1/c/{name}/outbox` Follow/Undo, change 148/earlier); the **inbound** follow accept/reject
     is done (change 152). Still open: the community **UI** screens (an "Inbound follows" card on the
     community page wiring the new endpoint) and the two-instance wire drive of a gated community's
     inbound follow — both live/UI-verification items.
- [ ] **19.5.4 — Community moderation surface.** Flag/block/mute at the community level where
   supported; verify the moderation collections and that moderated actors' content is excluded from
   the community feed (or record the gap).
   - [x] **Community-scoped moderation edges (19.5.4).** The community's own block/flag/mute sets
     (`ICommunityStore`, keyed by `communityIri` — distinct from the person `IModerationStore`): a
     community blocks/mutes/flags an actor without affecting any other community's feed. In-memory +
     file-backed (round-tripped through a `blocks`/`flags`/`mutes` section).
     (change 153)
   - [x] **The community feed excludes blocked/muted members' content (19.5.4).** A blocked member's
     content is excluded (hard) and a muted member's is excluded while the membership is kept (soft); a
     *flagged* member is **not** excluded (a flag is a report, not a filter — mirroring the person feed,
     where only blocks and mutes filter the timeline).
     (change 153)
   - [x] **The community moderation collections + mute endpoint (19.5.4).**
     `GET /ap/v1/c/{name}/{blocks|flags|mutes}` serves the community's moderation edges as a paged
     collection (mirrors the person collections for a `Group`); the community document advertises the
     three links; `POST /ap/v1/c/{name}/mutes/{target}` (Basic auth, the community's IRI is the
     credential seam) records/removes a community-scoped mute (`?unmute=true`). Block/flag are the
     federated `Block`/`Flag` activities (not a local POST).
     (change 153)
   - `remaining:` for full 19.5.4 — the **community UI** moderation screen (wiring the mute POST + the
     block/flag collection reads) and the two-instance wire drive of a signed `Block`/`Flag` addressed to
     a community (the federated half reuses the existing `Block`/`Flag` inbox-handler path, proven for the
     person level) — both live/UI-verification items.
 - [ ] **19.5.5 — Community feed correctness.** The unified feed (members' outboxes, newest first,
   de-duplicated) yields exactly the right activities: local member posts, remote content delivered to
   the community inbox (the catch-all recording into member outboxes), pagination, and `?refresh=true`
   cache bypass. Compare against the raw member outboxes to confirm no missing/duplicate items.
   `remaining:` the feed's **newest-first merge** was wrong and is now fixed + pinned (change 149):
   `ICommunityFeedService` documented "newest first" but actually concatenated the members' outboxes in
   member-IRI order (grouped by member), so a member's newest post did **not** rank above another
   member's older post. It now merges by (outbox position, then member IRI) — a stable, deterministic
   newest-first merge — while keeping the IRI de-duplication and the `?q` content filter. New
   `CommunityFeedCorrectnessIntegrationTests` pin the merge order, de-dup, and pagination; the existing
   `CommunityFeedIntegrationTests`/`CommunitySearchIntegrationTests` order assertions were updated to
    the new merge. The **remote-content** half of the feed (content delivered to the community inbox and
    propagated into member outboxes) is done + pinned by
    `RemoteContent_ToCommunityInbox_PropagatesToMemberAndAppearsInFeed`
    (`CommunityFollowingIntegrationTests`). The **`?refresh=true` cache bypass** is now done + pinned
    (change 154): the community collections (feed, members, following/followers, blocks/flags/mutes) are
    served through `LocalCollectionPageCache`, so a plain read caches the page
    (`max-age=60, stale-while-revalidate=300`) and `?refresh=true` bypasses it + emits `no-cache`; the
    feed's `?q` filter is part of the cache key. New
    `CommunityFeed_IsServedFromThePageCache_WithRefreshBypassAndCacheControl` pins the stale-within-TTL
    read, the refresh bypass, the write-back, and the `Cache-Control` values. Still open for full 19.5.5:
    the **community UI** feed screen (a sample-client screen that issues `?refresh=true` on a manual
    refresh) — a live/UI item.
- [x] **19.5.6 — Community lifecycle on recreation.** A community created in a prior turn (with
  members, follows, content) survives `down`/`up` (volume-backed) with all collections intact.
  **Resolved (19.5.6, change 155):** the file-backed stores round-trip the community's whole state —
  the community document, the members/follows/followers sets, the community-scoped moderation (block/
  flag/mute) sets (19.5.4), and the member outboxes the unified feed is derived from. Pinned by
  `Community_FullState_MembersFollowsFollowersModerationAndContent_SurvivesRestart` (a fresh
  `FileBackedPersistenceProvider` over the same directory, the `down`/`up` simulation, re-reads every
  collection unchanged). Still open for full 19.5.6: the live Docker `down`/`up` drive of a seeded
  community over the public FQDNs — a live-verification item.

### Phase 19.6 — Architectural expectations: client↔server interaction

Confirm the core architectural invariants of how clients talk to servers.

- [ ] **19.6.1 — Management via ActivityStream only.** Every management-style operation (create
  community, add/remove member, follow/unfollow, reject follow, like/unlike, boost/unboost, delete,
  flag/block/mute, relay add/remove) is expressible **and verified** as a signed ActivityStream/
  ActivityPub message through an outbox/inbox — no side channel. UI writes must show (raw inspector)
  that they are these messages; raw-HTTP direct-store writes are *not* a supported path.
   `remaining:` the CI-testable half is done (change 161g): `OutboxSingleSourceOfTruthIntegrationTests`
   now authors **every** supported ActivityStream management operation — Follow, Create, Like, Announce,
   Block, Flag, Undo (of Follow), Delete, Accept, Reject, Undo of Flag, Undo of Like, Undo of Announce,
   Undo of Block — and verifies the outbox contains exactly that set, each once, in stable order. Change
    161i adds the client's one-call **boost/unboost** (`AnnounceAsync`/`UnannounceAsync`, published to the
    actor's outbox through the signed pipeline) + two integration tests pinning the outbox recording — so
    every management operation is now expressible as a one-call client method (no side channel). Change
    161j wires the **Boost/Unboost button** into the object view (the UI half of the boost screen) + two
    in-process `S7ScreenTests` pinning the round-trips. Change 161k adds the client's one-call
    **community-maintenance** methods (`AddMemberAsync`/`RemoveMemberAsync` — an `Add`/`Remove` with
    `actor` = the community, delivered directly to the community's own inbox through the signed pipeline) +
     three integration tests, so the membership management operations are also one-call client methods.
     Change 161l adds the client's one-call **create-community** method (`CreateCommunityAsync` — a
     `Create` of a `Group` authored by a person to their **own outbox**; the server materializes the
     community in its community store on seeing the local `Group`) + three integration tests — resolving
     the previously-recorded create-community deferral (the chicken-and-egg of a community publishing to
     its own not-yet-existent outbox is avoided by having the creator's outbox carry the `Create`), so
     **every** management operation is now a one-call client method (no side channel). The
     local-moderation operations (Mute/Unmute,
    Relay/Unrelay) are non-AP (D4a) and go through `/local/v1`, not the outbox, so they are out of scope for
    this test. Still open: the **raw-inspector (UI) half** — drive every write screen (including the new
    boost/unboost button) through the browser and confirm the rendered signed message in the raw inspector
    matches the ActivityStream activity (a live/UI-verification item).
- [x] **19.6.2 — All activities flow through the outbox.** Every activity a local actor/community
  authors appears in that actor's/community's outbox collection (Follow, Accept, Create, Like,
  Announce, Undo, Delete, moderation) in a stable order; the outbox is the single source of truth for
  "what did this actor do." Verify by enumerating the outbox (UI + wire) after exercising every write
  screen and matching entries 1:1 with the actions taken.
   - [x] Server invariant pinned (`OutboxSingleSourceOfTruthIntegrationTests`): a single instance authors
     every supported activity type — Follow, Create, Like, Announce, Block, Undo, Delete (signed outbox
     publish) plus Accept and Reject (published to the followed actor's outbox, AP-native Phase 19.0b;
     the legacy follow-decision endpoint is removed) — and the actor's outbox contains exactly that
     authored set, each once, in the store's stable (newest-first) order; the HTTP outbox collection
     agrees with the persistence outbox.
  - [ ] Remaining (live, two-instance Docker env): the raw-inspector (UI) half — enumerate the outbox in
    the UI after exercising every write screen and match entries 1:1 with the actions taken.
- [x] **19.6.3 — Post-interact, server-delivers.** The client posts (publishes) an activity to the
  outbox and the **server** performs delivery to recipient inboxes (signed, per-actor), not the
  client. Verify: after a UI compose/follow/like, the peer's inbox received the activity with a valid
  signature from the acting actor; the client's own pipeline never made the cross-instance POST
  (inspect the delivery queue + peer logs/wire).
  - [x] Server-delivery invariant pinned (`OutboxPublishServerDeliversIntegrationTests`): a two-instance
    test drives the single-recipient `Block` path — the client makes a single signed POST to the
    author's own outbox (never the recipient's inbox) and the server delivers the `Block` to the
    blocked actor's inbox, signed as the acting actor; the peer validates the signature against the
    acting actor's document and records the edge (which only happens if the signature validated as the
    acting actor). Follow, Create, and Announce fan-out were already pinned (their own tests); the
    client invariant (every write → own outbox, no cross-instance POST) was already pinned in
    `ActivityPubClientTests`.
  - [ ] Remaining (live, two-instance Docker env): the raw-inspector (UI) half — drive compose/follow/like
    through the UI and confirm the peer's inbox received the activity with a valid acting-actor
    signature (also covers 19.6.4).
- [x] **19.6.4 — Signature identity.** Deliveries are signed as the *acting* actor (decision 029),
  resolvable by the receiver from the actor document (not the instance actor); the proxy path
  re-signs as the acting actor (decision 037). Verify with the raw inspector (key IRI in the
  `Signature` header matches the acting actor's `publicKey` id).
  - [x] Signature identity pinned (`OutboundSignatureIdentityIntegrationTests`), the acting actor
    distinct from the instance actor in each case: (a) the outbound delivery's `Signature` `keyid` is
    the acting actor's key IRI (`{actingActor}#key-1`, its `publicKey` id) — not the instance actor's —
    when a `DeliveryJob` carries a distinct acting actor (decision 029); (b) the proxy's re-signed
    request `keyid` is the authenticated (acting) actor's key IRI — not the instance actor's — when a
    browser authenticated as a second local actor posts a proxied request (decision 037). No production
    change — both per-actor signing paths (the `X-Iris-Actor` override resolved by the `SigningHandler`)
    were already implemented end-to-end; the existing `OutboundSignatureConformanceTests` could not make
    the assertion (its single actor is both the instance and the acting actor).
  - [ ] Remaining (live, two-instance Docker env): the raw-inspector (UI) half — drive a write through
    the UI and confirm the rendered `keyid` in the `Signature` header matches the acting actor's
    `publicKey` id; the server-side identity it exercises is already pinned in CI.
- [x] **19.6.5 — Audience correctness.** Outbound `Create`/`Announce` carry correct `to`/`cc`
  (followers + `as:Public` for public posts; the reply target for replies), and delivery recipients
  match the audience (followers' inboxes receive; non-followers do not).
  - [x] Delivery-recipient-audience match pinned (`OutboxAudienceMatchIntegrationTests`): a public
    `Create` (Note carrying `as:Public` in its `to`) and an `Announce` published to the author's own
    outbox are delivered to the author's remote followers' inboxes (the follower receives the activity,
    and the federated Note still carries `as:Public`) and NOT to a remote non-follower. This is the
    "delivery recipients match the audience (followers' inboxes receive; non-followers do not)" half,
    complementing the existing follower-fan-out and blocked-follower-exclusion pins. No production
    change — the follower-fan-out recipient computation already makes the recipients match the audience.
  - [x] Decision (change 158): Iris distributes by **follower fan-out**, not by enumerating `to`/`cc`.
    The server delivers an outbound Create/Announce as-authored and computes the recipient set from the
    author's remote, non-blocked followers; it does **not** rewrite the activity's `to`/`cc` to add every
    follower IRI (that would bloat the wire). A public post's audience is expressed by the client via
    `as:Public` (already pinned: `S7ComposeAudienceTests`, `ActivityPubClientTests`).
  - [ ] Remaining (would be a production change, deferred): server-side audience *metadata* — rewriting
    the outbound Create/Announce `to`/`cc` to enumerate the follower set, and adding the parent note's
    author (the reply target) to a reply's `to`/`cc`. The delivery already reaches the right inboxes;
    only the on-the-wire `to`/`cc` enumeration of the audience is left.
- [x] **19.6.6 — Cache behavior at the boundary.** Cached reads (collections, actor documents) expose
  `bypassCache`/`?refresh=true` and a new activity is visible after a bypass (the UI's refresh path
  actually re-fetches); no stale-forever behavior.
  - [x] Boundary pinned (`OutboxCacheBypassIntegrationTests`): a plain read of a just-cached outbox page
    serves the stale document (a brand-new activity added to the outbox is not yet visible), a
    `?refresh=true` read bypasses the cache and makes the new activity visible (emitting `no-cache`), and
    a subsequent plain read now sees it (the bypass wrote the fresh entry back — no stale-forever). The
    actor-document boundary is pinned the same way (public read cached, `?refresh=true` refetches +
    `no-cache`). No production change — the `?refresh=true` bypass (server) and
    `CollectionQuery.BypassCache` (client) were already implemented end-to-end; the adjacent tests
    covered the pieces (bypass mechanic, staleness on a community feed, cold-outbox visibility) but not
    the combined stale→bypass→fresh scenario.
  - [ ] Remaining (live, two-instance Docker env): the raw-inspector (UI) half — drive a write through
    the UI and confirm the refresh path actually re-fetches (a new activity is visible after the
    bypass); the server-side boundary it exercises is already pinned in CI.

### Phase 19.7 — Threads compatibility probe (Threads.net — best-effort)

Threads is very strict and hard to interact with; this is an **exploratory probe**: attempt the
baseline interactions, and **if we get stuck, make notes and move on** (record exactly where and why
— wire evidence of the rejection/failure — and continue).

- [ ] **19.7.1 — Discovery.** WebFinger `@mosseri@threads.net`; fetch the actor document (the
  explorer's object view / raw inspector); record the document's shape (key type — Threads uses
  Ed25519, so this exercises the EdDSA validation path), `@context`, and any non-standard properties.
- [ ] **19.7.2 — Follow.** Follow mosseri via the UI; observe the response (Accept? silent? error?)
  and what Threads' profile shows. If the follow is not accepted or our Accept is not consumable,
  record the wire exchange and stop this sub-item.
- [ ] **19.7.3 — Inbound content.** If following works (or is accepted), have a known Threads post
  arrive (or fetch a public post by IRI if discoverable) and verify our server stores it, renders it
  in the explorer's object view, and (if in a followed feed) surfaces it. Threads objects are
  heavily extended — verify the unknown-property passthrough does not reject them.
- [ ] **19.7.4 — Outbound content (best-effort).** Post a Note to our outbox addressed to the
  Threads audience (if the follow relationship exists) and observe whether Threads' delivery accepts
  it (their inbox 202? 401? 422?). Reply to a Threads post if the thread is discoverable (19.2.4
  baseline first). Record every outcome with wire evidence; **a stuck state is a valid outcome** —
  notes + `BLOCKED`/`GAP` classification, then move on to the next phase.
- [ ] **19.7.5 — Threads findings doc.** Consolidate the probe into a change doc: what works, what's
  rejected and why (signature profile? content type? context? audience shape?), and the minimal
  change list (deferred to 19.4 or a future phase) — no implementation in this phase.

### Phase 19.8 — UI navigability & rendering pass

The UI must be click-navigable end-to-end: selecting an item in a collection forwards to a **rendered**
view of the item, never raw JSON (the raw inspector remains an explicit, separate tool).

- [ ] **19.8.1 — Click-through audit (every collection).** From each collection surface (actor
  outbox, followers, following, liked, blocks/flags/mutes, relays, community members/feed/following,
  followed feed, home feed, search results, recent-instances), selecting an item navigates to the
  rendered view (ObjectPage/ActorDetail/Community) with the item's content, author, audiences, and
  relationships rendered — no raw-JSON dead ends. Record every collection→view transition that does
  not work.
  - **Audited live (161m, alice@iris-dev1, Playwright):** every *local* collection→view transition
    renders a proper view (no raw-JSON dead ends) — actor detail (outbox/moderation/follows/relays),
    home+followed feed, actor search results, community feed/members, and a local object deep-link
    (community→ObjectPage renders content + Like/Boost). Two gaps remain open (keep unchecked):
    (a) **remote (cross-instance) object reads CORS-fail in the browser** — the client dials the
    remote IRI directly (e.g. `mastodon.world/...`) with no same-origin proxy route, so a remote
    item in a feed cannot be opened (19.1.x live-interop); (b) the Instance page shows **no
    recent-instances** list. Minor UX: the Actor-detail `#handle` field rejects a full IRI with a
    confusing double-path "Not an actor" error. See
    `docs/changes/161m-19.8.1-click-through-audit.md`.
- [ ] **19.8.2 — Rendered object view quality.** ObjectPage renders: author (clickable to their
  actor detail), content HTML (sanitized), audiences (to/cc as handles), timestamp, reply chain
  (conversations view per 19.2.4), like/boost counts where available, and a link to the canonical
  public URL (on the originating instance) when the object is remote.
  `remaining:` **audiences (to/cc) + published timestamp** now render in the object view (change 161n):
  a new `IriExtensions.GetAudienceIris(this IObject?)` reads the object's `to`+`cc` (de-duplicated,
  the `as:Public` sentinel excluded) and the `ObjectView` renders each audience as a link to the
  recipient's actor page plus the `published` time (local). Core unit tests pin the audience read;
  new in-process `S19ObjectViewQualityTests` (bUnit) pin the rendered markup. Still open: **reply
  chain / conversations view** (deferred to 19.2.4), **like/boost counts**, and the **canonical
  public-URL link for remote objects** (needs the remote-read/proxying surface from 19.1.x).
- [ ] **19.8.3 — Actor detail completeness.** ActorDetail shows: the document's rendered fields
  (name, summary, icon/avatar, URL), every collection (with correct counts matching the wire),
  moderation controls (mute local / block / flag — federated), follow/unfollow, and the raw inspector
  as an explicit escape hatch (not the default view).
- [ ] **19.8.4 — Community view completeness.** Community screen: rendered group fields, members
  (clickable), feed (rendered items, clickable), following/followers (clickable), community
  follow/unfollow, and the management surfaces from 19.5 (creation, membership, peer management).
- [ ] **19.8.5 — Cross-instance navigation.** From iris-a's UI, selecting a peer (iris-b) item
  navigates to a rendered view of that item (fetched via proxy fallback or direct, per the dial
  config) — remote objects render, not just resolve; instance switching (recent-instances) preserves
  navigable state.
- [ ] **19.8.6 — Write-screen round-trips.** Every write screen (compose, follow, like, boost,
  reply, delete, unlike, unfollow, reject, moderation, relay) shows success state and the effect is
  visible on re-navigation without a full reload (collections update; the raw inspector shows the
  signed message sent).
   `remaining:` the **Boost** button is now wired (change 161j): the object view's write card offers a
   Boost/Unboost toggle next to Like/Unlike, calling the client's `AnnounceAsync`/`UnannounceAsync`
   through the signed pipeline, with the page's `WriteBusy`/`WriteResult`/`WriteError` machinery; the
   two new `S7ScreenTests` pin the boost + unboost round-trips in-process. Still open: the
   **live/UI-verification half** — drive every write screen (including boost) through the browser and
   confirm the success state + the raw-inspector signed message on re-navigation (a live/UI item).
- [ ] **19.8.7 — Error & empty states.** 404/unknown object, empty collections, failed logon,
  unreachable instance, and proxy-fallback failure each show a clear message (not a blank page or a
  raw error dump).
  `remaining:` error handling added to Actors, Community, Instance, and Feed pages (change 161h) —
  the four pages that previously let exceptions propagate unhandled. ObjectPage, ActorDetail, Home,
  and Compose already had error handling. Still open: the **live/UI-verification half** — drive each
  error state (404 object, empty collection, failed logon, unreachable instance, proxy-fallback
  failure) through the UI and confirm the rendered message is clear (not a blank page or raw error
  dump); the 404-object and proxy-fallback states are already exercised in CI (S10, S8).

**Definition of done for Phase 19 overall:** the full evaluation checklist (19.0.5) runs clean over
 the public FQDNs (PASS on every non-tabled waypoint, or a documented GAP/BLOCKED), the volume-backed
 stack recreates without data loss or delivery storms, and the findings register (19.4.1) is triaged
 with every FAIL fixed and re-verified.

## Phase 20 — End-to-end usage-story alignment (in planning; work item-by-item)

> Operator directive (this phase): **tighten the end-to-end architecture so we don't do massive
> overhauls later** — think each topic through thoroughly, deal with items **one by one**, and align
> each change with the **end-to-end usage story** (a user drives the sample explorer as a client of a
> local or remote instance). Items are **prioritized** so the load-bearing architectural decisions
> (C2S flow, auth, CORS/proxy, collections, the C2S inbox design) land before the UI/feature work that
> depends on them. The **manual test plan** is deliberately **last** (20.7): it confirms the finished
> architecture against the sample UI + wire, not the other way around. **Testing discipline:** the suite
> is growing too fast — prefer **a few integration tests that genuinely exercise the concepts** (outbox
> write → delivery → peer record; collection → paged items → object view; proxy fallback; inbox
> attachment rewrite) over a stack of thin unit tests; and **20.5 audits the suite** to remove the
> useless ones. Sub-items are drafted from the operator's message (2026-09-02) and will be refined into
> their own change docs as each is executed.
 >
 > **Operator directive (verbatim, 2026-09-02 — preserved for context compaction):**
 >
 > "We made some fairly major changes recently, see the git history and design records for details. We
 > need to develop a **manual test plan** that confirms our changes; I want to use the **sample UI** to test
 > various aspects. I want to **identify useless automated tests and remove them**. I want to confirm
 > **architectural cohesion** across the library: confirm the intended operations, like the way **C2S should
 > work via the user's outbox**, confirm **authentication via digest signature** works, confirm **proxy
 > fallback for CORS-related issues** works, confirm we can **browse external collections and
 > access/display items** in the sample UI. We want to **enhance the sample UI** to support more object
 > browsing — for example, I should be able to **enumerate a local or remote user's outbox and page through
 > it**. We need to **enhance our implementation to support media and sensitivity extensions**. Our sample
 > needs a **markdown viewer** so content that contains markdown can be rendered properly. We need to
 > **confirm our collections yield the correct objects** — I think **outbox should return creates**. We need
 > to review how we intend on handling **local-client user browsing their inbox in a C2S scenario**: how do
 > we ensure the content in the user's inbox is accessible by the browser; if a user receives a note with an
 > attachment, how will we yield that back when enumerated from the inbox; do we **store content and rewrite
 > the attachments** so a local interface can serve them without worrying about CORS; are the ids yielded
 > from the user's inbox **rewritten local ids** that, when requested, serve a rewritten version of the note;
 > how do we **sync replies and accumulate likes or announcements/boosts** — should we **pull and sync an
 > object each time we encounter it** to make sure we have a high-fidelity copy and accurate interaction
 > counts? Let's develop some plans to address these, work them into **new roadmap/plan items** so we don't
 > forget. We need to **tighten up the end-to-end architecture** around all of this so we don't find
 > ourselves doing massive overhauls later. We need to think thoroughly about these topics and **deal with
 > them one by one**. Once we have a plan of what to think about, we approach each item and make changes to
 > align with the **end-to-end usage story**. We need to perform **real-world tests against our sample
 > explorer** and ensure our client supports the intended use cases. The plan should be **prioritized in a
 > way that makes sense**. We should **save our manual testing for the end**, but try to avoid adding a ton
 > of tests — the test suite is growing fast and we should strive to generate **integration tests that really
 > exercise all of these concepts without over-stacking the suite**."
 >
 > **Operating mode (verbatim, 2026-09-02, granted for the loop):** "Additionally we are allowed to perform
 > **deeper investigations and increase the scope a bit if needed**. We want to **avoid asking questions of the
 > user and go with what you recommend**. **Document as you go along** to keep track of what we did,
 > **reorganize if needed**, and **skip any hard blockers or circle back** if you run out of work."
 >
 > **Ordering rationale:** 20.0 closes the in-flight decision-055 work (it gates a green build/suite, so
> it must land first). 20.1 confirms the four load-bearing C2S pillars (outbox, digest auth, proxy
> fallback, external-collection browsing) and decides the **outbox-returns-Creates** question — these
> are the "how does C2S actually work" facts every later item builds on. 20.2 is the **C2S inbox
> design** (the deepest open question: how inbox content is browser-accessible, attachment storage +
> CORS rewrite, local-id rewrite, reply/like/boost sync, pull-on-encounter fidelity) — it is a design
> decision first, code second. 20.3 is the **sample-UI browsing** enhancement (outbox enumeration +
> paging, local or remote) that makes 20.1/20.2 visible. 20.4 is the **implementation feature work**
> (media, sensitivity, markdown viewer). 20.5 is **test triage** (remove the useless, keep the
> integration-first few). 20.6 is the **architecture-cohesion pass** (re-confirm the whole story holds
> after the changes). 20.7 is the **manual test plan** (sample UI + wire) — the capstone, done last.

- [x] **20.0 — Close decision-055 (in-flight, gates a green build/suite).** Server is the sole
  object-id authority: server mints ULIDs for all authored objects (dependency-free
  `Iris.Core/Identity/Ulid.cs` + `Iris.Server/Identity/IdMinter.cs`), the outbox handler accepts an
  id-less `Activity`, mints the id, and **returns the created object in the 2xx body**; the client
  drops `Id` from every authoring method and references the **learned** (server-minted) id it got back
  (`DeliveryResult.MintedId`); reference-carrying methods (Undo/Accept/Reject) take the learned id,
  never a recomputed formula. **Done (all):** ULID + IdMinter, outbox handler (mints + 2xx body via
  `Results.Text`), `MintActivityIds` (preserves client-set embedded ids, reassigns `Create.Object`),
  inbound helpers (`FollowIris`/`AnnounceIris`/`DeleteActivityHandler` + `FollowActivityHandler` now
  inject `IdMinter`), the `ICreateIndex` (object→Create), `IActivityStore.GetAllActivitiesAsync`,
  client rework (id-less authoring + learned-id inverses + Add/Remove repointed to outbox),
  `IActivityPubClient`/`DeliveryResult`, and the sample callers (learned-id flow). **Test close-out:**
  the ~10 `Iris.Server.Tests`/`Iris.Client.Tests` files that predicted ids by the old formula now build
  id-less helpers and **learn** the minted id (from the 2xx body, the stored outbox, or by enumerating
  the activity store) — the `FollowEdgeConvergence` and `FederationSignature` tests locate minted
  objects by reference rather than a computed IRI. `dotnet test` green: **1,111 tests, 0 failed**
  (Core 210 / Client 135 / Server 766; was 5 failing at 20.0 start). Change doc
  `docs/changes/161p-055-server-is-object-id-authority.md`.
- [x] **20.1 — Confirm the four C2S load-bearing pillars + decide outbox-returns-Creates.**
  (a) **C2S via the user's outbox:** CONFIRMED — every client authoring method posts to
  `actorId.OutboxOf()` (zero inbox call sites); the outbox-publish handler records the activity in the
  actor's outbox + activity store, and the embedded object in the object store (outbox & object store
  agree). (b) **Digest auth:** CONFIRMED — the browser's signed POST verifies (161o `X-Signature-Date`
  live-verified; signer + verifier share `ResolveDateComponent`; federation round-trips green). (c)
  **Proxy fallback for CORS:** CONFIRMED with the trigger made explicit — the fallback is a
  *signature-rejection* fallback (engages on **401/403**, the case where the remote can't validate the
  browser's WebCrypto signature), **not** a network/CORS-failure fallback (a CORS-blocked browser fetch
  throws with no status, so it propagates, not re-routed through the proxy); CORS-blocked *writes* use the
  always-proxy mode (same-origin proxy) and CORS-blocked *reads* rely on the remote CORS-opening its AP
  routes. Pinned by the new `DirectNetworkFailure_Propagates_NotRoutedThroughProxy` test. (d) **Browse
  external collections:** CONFIRMED — `GetCollectionAsync`/`GetCollectionItemsAsync` are host-agnostic
  (any IRI, paged next-links); ActorDetail (outbox) + ObjectPage (replies) render remote IRIs; Feed/
  Community are local-only by design (dial-base derived). **(e) Outbox-returns-Creates:** CONFIRMED as the
  operator's expectation — the outbox yields **`Create`** activities (the post is the Create; the embedded
  Note is the object), the object is a first-class object addressable by its (originator-)minted id, and
  the 2xx body returns the Create. No divergence from current behavior. Change doc
  `docs/changes/161q-20.1-c2s-pillars-confirmed.md`; +1 proxy test (1,112 tests, 0 failed).
- [x] **20.2 — C2S inbox design (design decision first, code second).** How does a **local client user
  browse their own inbox** in a C2S scenario, and how is that content **accessible to the browser**?
  Decision 056 resolves all five sub-questions and the foundation is implemented: (a) **Browser access** —
  the inbox is a private, **owner-only** `GET /ap/v1/u/{handle}/inbox` (Basic auth via the existing
  `IActorCredentialValidator` seam; 403 for a non-owner; no-store); (b) **Content + attachments** — the
  inbound object is **stored locally verbatim** (the object store already does this under the originator
  id); remote attachments are **served verbatim (link-out)** for now, with local media storage + URL-rewrite
  staged to **20.4 (media)**; (c) **Id rewrite** — **none**: inbound objects keep the **originator's id**
  (decision 055); the inbox yields them by that id and a request for it serves the local copy; (d) **Reply +
  like/boost sync** — **out of scope** for the foundation (surfaced as follow-up; the inbox surfaces the
  delivered activities; thread-tree + per-object counters are separate, self-contained slices); (e)
  **Pull-on-encounter fidelity** — **store-on-receive** (the current, higher-fidelity-than-TTL behavior);
  fetch-on-encounter of *referenced-but-not-delivered* objects is a follow-up. **Implemented:** the inbox is
  now a first-class, per-actor collection (`IActivityStore.GetInboxAsync`/`AddToInboxAsync` + both impls),
  recorded on first delivery by the `InboxProcessor`, read via the owner-only route + `GetInboxItemsAsync`
  client method. **Remaining (staged, self-contained):** local media rewrite (20.4), reply thread-tree,
  per-object like/boost counters, fetch-on-encounter. `docs/decisions/056-c2s-inbox-model.md`;
  `docs/changes/161r-20.2-c2s-inbox-design.md`; +4 inbox tests (1,116 tests, 0 failed in the suite that
  runs; 2 pre-existing `FollowIntegrationTests` failures are unrelated to this change).
- [x] **20.3 — Sample-UI object browsing: outbox enumeration + paging (local or remote).** Enhance the
  explorer so a user can **enumerate a local or remote user's outbox and page through it** (the paged
  collection with `next`-link paging), rendering each item as a navigable object view (not raw JSON).
  This makes 20.1's "outbox = source of truth" and 20.2's inbox design visible in the UI. Reuse the
  existing paged-collection client + object view; add an "Outbox" surface to the actor detail page (local
  + remote) with paging controls. **Done** — the actor detail page's non-paged "Feed (outbox)" card is
  replaced by a paged "Outbox" surface (first page via `GetCollectionAsync` against `{actor}/outbox`, a
  "Load more" button walking `next`-linked server pages, each item a navigable `<ObjectView>`); local or
  remote (host-agnostic paging). +4 integration tests (`S20OutboxPagingTests`); see
  `docs/changes/161s-20.3-outbox-enumeration-paging.md`.
 - [x] **20.4 — Implementation feature work: media, sensitivity, markdown rendering.** (a) **Media:**
   support media attachments end-to-end (compose upload → Create object with attachment → stored/served →
   rendered in the object view), building on 20.2's attachment-rewrite decision. (b) **Sensitivity
   extension:** support the content-sensitivity / warning extensions (e.g. `sensitive`/`summary`) so a
   sensitive object renders behind a blur/notice with its summary until the user reveals it. (c) **Markdown
   viewer:** the object view currently renders `content` as-is; add a **markdown renderer** so content
   that is markdown (rather than HTML) renders properly (links, code, lists, headings) — pick the approach
   (a dependency-free renderer or a small markdown lib; note any new NuGet in this file per the rules).
    **Done — (a) media + (b) sensitivity + (c) markdown all complete.**
    **(a) Media:** a note's attachment is uploaded to the author's instance (a local, Basic-authenticated
    multipart POST, `POST /local/v1/u/{handle}/media`, owner-only, 10 MiB cap) which stores the bytes and
    returns (201) the same-origin media IRI; the uploader sets that IRI as the `url` of an `Image`
    attachment on the note it posts. `GET /ap/v1/media/{id}` serves the bytes back from the same origin
    (long-cacheable), so the browser loads the attachment from the same origin, never a cross-origin media
    host (Decision 056 (b)). Storage: `IMediaStore` (in-memory + file-backed, wired into both persistence
    providers). Client: `IMediaClient` (Basic-auth multipart POST) + `PostNoteAsync(Iri, Note, ct)`.
    `IriExtensions.GetMediaAttachments` is the single boundary read of an object's `Image` attachments →
    same-origin media IRIs. The sample's compose screen uploads via `InputFile` and the object view renders
    the attachment `<img>`. +23 tests (6 `MediaClientTests`, in-memory + file-backed store tests,
    upload/serve integration, round-trip integration). Full suite 1,371/0; see
    `docs/changes/161v-20.4-media.md`.
    **(b) Sensitivity:** a content-sensitive object (`sensitive` term + `summary`) renders behind a notice
    in the object view, its real content absent from the DOM until the viewer reveals it —
    `IriExtensions.IsSensitive`/`GetSummary` (Core) read the term (`sensitive` surfaces via the library's
    `ExtensionData`, per the 3rd-Party rules; `summary` is typed) + the object view's show/hide toggle.
    +17 tests (10 Core read-helper, 5 bUnit object-view, 2 integration round-trip); see
    `docs/changes/161t-20.4-sensitivity.md`.
    **(c) Markdown:** a note's `content` that is Markdown now renders properly (headings, inline/fenced
    code, links, lists, emphasis) via a small **dependency-free** `Markdown` renderer (sample-local — a
    deliberate choice over adding a markdown NuGet just for a sample-UI feature; **no new package**). It
    HTML-escapes first (raw markup inert) and sanitizes link targets to http/https/mailto (a
    `javascript:` scheme renders as text, never a live link). The object view renders `content` through it
    (emitted as a `MarkupString`); the sensitive-notice path renders the markdown only after reveal. +32
    tests (25 `MarkdownTests` unit, 7 bUnit object-view); see
     `docs/changes/161u-20.4-markdown.md`.
   - [x] **20.4(d) — Browser-loadable media for *external* attachments (the inbound/external-read half of
    media, deferred by 20.2/056 (b) and scoped out by 161v).** **Decision 057.** When the client browses any
    **external** stream — a random user's outbox, a user's liked/featured, communities, the followed feed,
    synced peers, **and** the owner-only inbox — an object's attachment can carry a **cross-origin** `url`
    that the browser cannot load (the remote host sends no CORS headers → the image is blocked). **Design
    (decision 057):** the **wire stays 100% AP-native/verbatim** in every collection; the **client's render
    boundary** rewrites each cross-origin attachment `url` to a same-origin proxy IRI
    (`{base}/ap/v1/media/proxy?url={originator-url}`) — the **URL is the key** (the client always has it, so
    it works for cold external objects a content hash could not cover); a new **public, long-cacheable**
    `GET /ap/v1/media/proxy?url=…` route **fetches-once, stores, serves same-origin** (502 + client link-out
    fallback on fetch failure); the media store gains a **URL key** + a **content-hash dedupe index** (the
    hash is **server-internal** — the client never computes or uses it); **eager-warm is on by default**
    (a `MediaOptions.EagerWarm` flag) — when the server *stores* an object with attachments (inbound/
    followed/community/synced) it pre-fetches them by URL so the proxy serves instantly; cold external
    browsing is lazy-by-nature (warmed on first proxy hit). A small `IMediaFetcher` seam (unsigned outbound
    fetch, reusing the existing `IHttpClientFactory`-backed pattern) + the client rewrite helper. **No new
    NuGet; no id/document rewrite (055 + 056 (c) unchanged); no collection-serializer rewrite.** Vertically
    complete: fetcher + route + URL/hash store + client render boundary + eager-warm hook + integration
    tests (proxy fetch/serve + cache hit + 502 fallback + client rewrite unit + an end-to-end
    "browse external outbox → attachment loads same-origin"). +17 tests (9 `IriMediaProxyExtensionsTests`
    client-rewrite unit, 8 `MediaProxyIntegrationTests`: proxy fetch/serve, cache-hit no-refetch, 502
    fallback, 400 invalid url, public no-auth, eager-warm, eager-warm-disabled-lazy); see
     `docs/changes/161y-20.4d-external-media.md`.
    - [x] **20.4(e) — Per-object interaction counters (like / boost counts), deferred from decision 056 §d.**
     A content object's per-object **like** and **boost** counters. **The source of truth is the reverse
     indexes** (object → set of actor IRIs), **not a wire rewrite** (the object document stays verbatim, per
     055/057). The wire exposes the counts as **extension collections under the bare, non-namespaced terms
     the ActivityPub ecosystem uses** for object-side interaction collections (Mastodon/Pleroma/Misskey all
     use bare `likes`/`shares`, never namespaced): `GET {object-iri}/likes` and `GET {object-iri}/shares`,
     each a **full, non-paged `OrderedCollection`** of actor `Link`s with `totalItems` = the count (a
     like/boost set is small and bounded, unlike an outbox). These are **extension collections, not core AS
     `Object` properties** (the only core object collection is `replies` — likes/shares exist only as
     `Like`/`Announce` activities); per the ActivityStreams extensibility rule a strict consumer that does not
     know the term **MUST ignore it**, so the bare terms are safe. Discoverability is via the
     `iris:capabilities` extension (`CapabilityLikes`/`CapabilityShares`). **Changes:** `ILikeStore` gains
     `GetLikersAsync` (both directions maintained on record/remove — in-memory + file-backed, the
     file-backed store's `{"edges","likedBy"}` form is backward-compatible with the earlier bare-array form);
     a new `IAnnounceStore` (`RecordAnnounceAsync`/`RemoveAnnounceAsync`/`GetAnnouncedAsync`/
     `HasAnnouncedAsync`/`GetAnnouncersAsync`, the `shares` reverse index — in-memory + file-backed
     `announces.json`); `IPersistenceProvider.Announces` (wired into both providers + DI);
     `AnnounceActivityHandler` records the announce edge; `UndoActivityHandler` gains the previously-missing
     `Undo(Announce)` branch (an un-boost removes the edge + the boost's outbox entry); the
     `ObjectDocumentHandler` dispatches `/likes` + `/shares`; the client's `IriExtensions.LikesOf()`/
     `SharesOf()` + `IActivityPubClient.GetLikesAsync`/`GetSharesAsync` read the count by enumerating the
     collection; the object view shows the counts next to the Like/Boost buttons (null for external objects —
     no count shown, not 0). **No new NuGet; wire stays verbatim (055/056 (c) unchanged).** Vertically
     complete: stores + handlers + routes + client + UI + integration tests (the `/likes` + `/shares` routes,
     the empty + 404 cases, and the client round-trip). +30 tests (`InMemoryAnnounceStoreTests` new,
     `InMemoryLikeStoreTests` reverse-index, `FileBackedPersistenceTests` announce-store restart + reverse-
     index survival, `UndoActivityHandlerTests` `Undo(Announce)`, `InteractionCollectionIntegrationTests`
     new); suite 1,342 → 1,372 (0 failed); see
      `docs/changes/161z-20.4e-interaction-counters.md`.
  - [x] **20.5 — Test-suite triage: remove the useless, keep the integration-first few.** Audit the test
   suite (growing too fast) and **remove tests that add no coverage** (duplicates, over-fine unit tests
   of trivial glue, tests that pin implementation details that changed with 055). Keep/grow the
   **integration tests that genuinely exercise the concepts** (outbox write→delivery→peer record;
   collection→paged items→object view; proxy fallback; inbox attachment rewrite; digest auth). Record the
   removed/added set + new count in a change doc; target a **smaller or stable** count with higher
   per-test value, not growth. **Done — removed 34 over-fine null-guard tests (Iris.Server.Tests) that
   only asserted `ArgumentNullException.ThrowIfNull` (trivial BCL glue, no Iris coverage); no duplicates and
   no pre-055 id pins existed (the suite is already Decision-055 compliant). Every "throws" test asserting a
   real invariant (blank Id, negative window, missing credential, wrong activity type) and every
   integration test kept. Suite: 1,371 → 1,337 (0 failed); see `docs/changes/161w-20.5-test-triage.md`.**
 - [x] **20.6 — Architecture-cohesion pass (re-confirm the whole story after the changes).** Re-verify,
  end to end, that the intended operations hold **after** 20.0–20.5: C2S via the outbox, digest auth,
  proxy fallback for CORS, external-collection browsing + display, outbox-returns-Creates, the C2S inbox
  design (browser access + attachment rewrite + id rewrite + reply/like/boost sync + pull-on-encounter),
   media + sensitivity + markdown. Confirm no layer contradicts another (client ↔ server ↔ sample UI) and
   no "massive overhaul" is lurking. Output: a short coherence note (a change doc) listing each pillar →
   confirmed/changed + any follow-up. **Done — all 9 pillars confirmed consistent across client/server/
   sample UI; no contradictions, no stale pre-20.0 id computation, no overhaul lurking. One documented
   deferral (inbound/federated attachment URL-rewrite, scoped out by 161v) recorded as a known open item.
   See `docs/changes/161x-20.6-cohesion-pass.md`.**
- [ ] **20.7 — Manual test plan (sample UI + wire) — the capstone, done last.** Develop and execute a
  **manual test plan** (Playwright-MCP-driven, as in Phase 19's method) that **confirms the recent major
  changes** using the sample UI + wire evidence: cover every 20.x capability from a user's seat (log on,
  compose with media + markdown, post → outbox returns the created object with its minted id, browse own +
  peer outboxes with paging, like/boost + see counts sync, receive in inbox → browser-accessible with
  local attachment serving, sensitive-object reveal, proxy fallback on a CORS-failing remote read). Record
  each as a PASS/FAIL/GAP/BLOCKED checkpoint in a change doc. This is the **last** item — it confirms the
  finished architecture, not the reverse.

## Remaining work (pre-Phase-19 carry-forward, now superseded)

- ~~Phase 13.5–13.7, 13.9–13.10 (live interop)~~ → **Phase 19.1** (the FQDNs are live; execute now).
- ~~Phase 14 (remediation)~~ → **Phase 19.4**.
- **Sample polish (non-blocking)** — media/attachment upload in the explorer compose screen (deferred
  from the 2nd-round plan; all other screens are live-verified). Re-check in 19.8.6.

## Notes

- CI gate: `dotnet build` clean (warnings = errors) + `dotnet test` green (1180+ tests across 8 projects).
- No new NuGet packages without a note here + justification (see [CODING_STYLE](reference/CODING_STYLE.md)).
- Phase 19 is **manual/live** by design: its "tests" are the Playwright-MCP-driven UI sessions + wire
  verification + the change-doc checkpoint tables, not new `dotnet test` entries. Library/sample code
  changes made *because of* Phase 19 findings still ship with their normal integration tests (per the
  loop's Definition of Done) — the evaluation itself is not a test-framework addition.
- External community-style interaction testing is **tabled** (see the Phase 19 header).
- Doc placement rules: [reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
