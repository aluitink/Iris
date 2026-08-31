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

- [ ] **19.0.1 — Volumes for server state.** Add named Docker volumes (or bind mounts) for
  `iris-a`/`iris-b` state in `docker-compose.yml`; switch the sample server to opt-in
  `UseFileBackedPersistence` (Phase 16.4) behind an env var (default stays in-memory), with the volume
  as the persistence directory; the delivery queue (Phase 16.2) uses the same volume. Verify:
  `docker compose down` (no `-v`) → `up` → actors, keys, follows, outbox content, and pending delivery
  all survive; a fresh `down -v` + `up` resets cleanly.
- [ ] **19.0.2 — Seed determinism + idempotency.** Seeding must be safe to run against a non-empty
  volume (idempotent by IRI, never duplicates actors/notes across recreations) and must not clobber
  state created during testing (e.g. a follow made in a prior turn survives a recreation).
- [ ] **19.0.3 — FQDN + TLS + CORS audit.** Verify end-to-end over the public FQDNs: WebFinger on both
  instances, the UI origin in `IRIS_CORS_ORIGINS` matches the UI's actual origin, advertised IRIs are
  clean `https://iris-devN.luit.ink/...` (no port), and the peer instances' `Iris__PeerBase` resolves
  each other's *public* IRIs (not just the in-network names) so federation works after a volume-backed
  recreation. Fix whatever is miswired; smoke via `scripts/docker-smoke-test.sh`.
- [ ] **19.0.4 — Test-account readiness.** Confirm `@RayvenMX@mastodon.world` is resolvable via
  WebFinger from our instances, its actor document fetches + key validates, and our sample actors'
  Basic-auth logon works from the public UI origin. Record the account's capabilities (posting,
  follows) as the known-good external reference.
- [ ] **19.0.5 — Evaluation checklist scaffold.** Create `docs/reference/LIVE_EVALUATION_CHECKLIST.md`
  (or extend the INTEROP_TEST_HARNESS §4a checklist) as the standing manual checklist the Playwright
  sessions execute: the standing checklist (logon, explore, switch instance, cross-instance write,
  moderate, external instance) + every Phase 19 waypoint mapped to a UI path. This is the operator's
  (and the agent's) repeatable routine between turns.

**Definition of done for 19.0:** stack recreated twice with `down`/`up` (no `-v`) with zero data loss;
smoke test green over the FQDNs; the checklist doc committed.

### Phase 19.1 — Live interop verification (was Phase 13.5–13.10 + 14 execution)

Execute the [compatibility matrix](reference/COMPATIBILITY_MATRIX.md) scenarios against the live
network, through the UI where a UI path exists and over the wire for server-side behavior. The
predicted gaps (§5 of the matrix) are re-checked against the *current* code (several were since closed
by later phases — outbound `Create`, `Undo`, group-follow, global search, EdDSA, person-inbox `Create`
handling exist now — so expectations must be re-derived from source before each scenario).

- [ ] **19.1.1 — Iris↔Iris baseline.** alice@iris-dev1 ↔ alice@iris-dev2: follow (UI), Accept round-trip
  (wire: both outboxes), unfollow via `Undo` (edge removed on both sides), like, post+reply (peer's
  inbox received the `Create`), community follow, community post surfacing on the peer. This is the
  "sanity check before external platforms" baseline.
- [ ] **19.1.2 — Follow scenarios (F1–F4)** against `@RayvenMX@mastodon.world`: they follow us → we
  `Accept` (wire: their inbox; UI: our followers collection); we follow them (UI) → their `Accept`
  arrives and is recorded; `Reject` behavior (our local-follow-reject endpoint → does the peer see a
  `Reject`?); unfollow via `Undo` (does Mastodon remove the relationship? check their profile UI).
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

- [ ] **19.3.1 — Follow-loop safety.** Mutual follows (alice-a ↔ alice-b) must not re-deliver
  activities back and forth: post on A → lands in B's inbox exactly once → appears in B's stores
  exactly once → B does **not** re-deliver A's post to A (no forwarding of already-local content).
  Verify by counting occurrences in outboxes/stores after each recreation and after repeated posts.
- [ ] **19.3.2 — Echo/amplification check.** With both instances following each other (and the
  community following the peer), post once; enumerate every delivery event (delivery queue + peer
  inboxes) and assert the total is bounded (no quadratic growth, no re-announce of announces).
  Specifically: an `Announce` (boost) of a peer's post must not be re-announced by the peer (boost
  loops are the classic federation failure).
- [ ] **19.3.3 — Announce propagation.** Boost a note on A; the boost reaches B's followers once;
  boost a note *from* B on A (boosting remote content) — verify no infinite announce chain and the
  correct `object` link (not an embedded copy that could double-attribute).
- [ ] **19.3.4 — Delete propagation, both directions.** Delete a local note → peer tombstones it;
  delete a note *originating* on the peer (if our instance can delete remote-originated content,
  e.g. a local reply to their note) → correct scope, no collateral deletion.
- [ ] **19.3.5 — Follow-edge convergence.** After a follow/unfollow/re-follow cycle across the two
  instances, both sides' `following`/`followers` collections converge and agree (same IRIs, same
  counts, stable pagination) — no orphan edges, no duplicate edges.
- [ ] **19.3.6 — Update propagation.** Update (re-publish with new content, same IRI) one of our
  notes → the peer's stored copy is updated (or correctly ignored if we don't implement Update
  handling — record which, and whether the object endpoint serves the new content).
- [ ] **19.3.7 — Recreation stability.** Run the 19.3.1–19.3.5 sequence, `down` (no `-v`) + `up`, and
  re-verify: no re-delivery storms on boot (queued deliveries replay at most once), no duplicated
  edges, outboxes unchanged in length.

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
- [ ] **19.5.2 — Membership management.** Add/remove members via management-style activity messages
  (not direct store writes): an actor joining (an `Add` to `members`, or a Follow-based join if that's
  the chosen model — record the decision in a decisions doc), leaving (inverse), and the community
  feed reflecting membership changes.
- [ ] **19.5.3 — Community peers (following management).** The community follows a remote actor/
  community via `POST /ap/v1/c/{name}/outbox` (Follow) — verify the edge, the `following` collection,
  and delivery to the target; unfollow via `Undo` (edge removed, peer notified); reject/undo flows
  for inbound follows of the community (we reject a follow → the peer sees `Reject`).
- [ ] **19.5.4 — Community moderation surface.** Flag/block/mute at the community level where
  supported; verify the moderation collections and that moderated actors' content is excluded from
  the community feed (or record the gap).
- [ ] **19.5.5 — Community feed correctness.** The unified feed (members' outboxes, newest first,
  de-duplicated) yields exactly the right activities: local member posts, remote content delivered to
  the community inbox (the catch-all recording into member outboxes), pagination, and `?refresh=true`
  cache bypass. Compare against the raw member outboxes to confirm no missing/duplicate items.
- [ ] **19.5.6 — Community lifecycle on recreation.** A community created in a prior turn (with
  members, follows, content) survives `down`/`up` (volume-backed) with all collections intact.

### Phase 19.6 — Architectural expectations: client↔server interaction

Confirm the core architectural invariants of how clients talk to servers.

- [ ] **19.6.1 — Management via ActivityStream only.** Every management-style operation (create
  community, add/remove member, follow/unfollow, reject follow, like/unlike, boost/unboost, delete,
  flag/block/mute, relay add/remove) is expressible **and verified** as a signed ActivityStream/
  ActivityPub message through an outbox/inbox — no side channel. UI writes must show (raw inspector)
  that they are these messages; raw-HTTP direct-store writes are *not* a supported path.
- [ ] **19.6.2 — All activities flow through the outbox.** Every activity a local actor/community
  authors appears in that actor's/community's outbox collection (Follow, Accept, Create, Like,
  Announce, Undo, Delete, moderation) in a stable order; the outbox is the single source of truth for
  "what did this actor do." Verify by enumerating the outbox (UI + wire) after exercising every write
  screen and matching entries 1:1 with the actions taken.
- [ ] **19.6.3 — Post-interact, server-delivers.** The client posts (publishes) an activity to the
  outbox and the **server** performs delivery to recipient inboxes (signed, per-actor), not the
  client. Verify: after a UI compose/follow/like, the peer's inbox received the activity with a valid
  signature from the acting actor; the client's own pipeline never made the cross-instance POST
  (inspect the delivery queue + peer logs/wire).
- [ ] **19.6.4 — Signature identity.** Deliveries are signed as the *acting* actor (decision 029),
  resolvable by the receiver from the actor document (not the instance actor); the proxy path
  re-signs as the acting actor (decision 037). Verify with the raw inspector (key IRI in the
  `Signature` header matches the acting actor's `publicKey` id).
- [ ] **19.6.5 — Audience correctness.** Outbound `Create`/`Announce` carry correct `to`/`cc`
  (followers + `as:Public` for public posts; the reply target for replies), and delivery recipients
  match the audience (followers' inboxes receive; non-followers do not).
- [ ] **19.6.6 — Cache behavior at the boundary.** Cached reads (collections, actor documents) expose
  `bypassCache`/`?refresh=true` and a new activity is visible after a bypass (the UI's refresh path
  actually re-fetches); no stale-forever behavior.

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
- [ ] **19.8.2 — Rendered object view quality.** ObjectPage renders: author (clickable to their
  actor detail), content HTML (sanitized), audiences (to/cc as handles), timestamp, reply chain
  (conversations view per 19.2.4), like/boost counts where available, and a link to the canonical
  public URL (on the originating instance) when the object is remote.
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
- [ ] **19.8.7 — Error & empty states.** 404/unknown object, empty collections, failed logon,
  unreachable instance, and proxy-fallback failure each show a clear message (not a blank page or a
  raw error dump).

**Definition of done for Phase 19 overall:** the full evaluation checklist (19.0.5) runs clean over
the public FQDNs (PASS on every non-tabled waypoint, or a documented GAP/BLOCKED), the volume-backed
stack recreates without data loss or delivery storms, and the findings register (19.4.1) is triaged
with every FAIL fixed and re-verified.

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
