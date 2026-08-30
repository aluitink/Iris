# Iris — Roadmap

This is the shortened working roadmap. The detailed phase notes and design evidence live in [changes/](changes/README.md) (one doc per slice) and [decisions/](decisions/README.md) (substantial design calls).

## Status at a glance

| Phase | Status |
|---|---|
| -1 — Project Reorganization | ✅ complete |
| 0 — Scaffolding | ✅ complete |
| 1 — Core: Identity, Keys, Signatures & Caching | ✅ complete |
| 2 — Client Library | ✅ complete |
| 3 — Server Foundation | ✅ complete |
| 4 — Inbox & Delivery | ✅ complete |
| 5 — Community / Group Support | ✅ complete |
| 6 — Proxy Fallback | ✅ complete |
| 7 — Blazor Client Extensions & Samples | ✅ complete |
| 8 — Sample Docker Composition | ✅ complete |
| 9 — Real-World Deployment Preparation | ✅ complete |
| 10 — Project & Test Review | ✅ complete |
| 11 — Implementation Gaps & Usability Exploration | ✅ complete |
| 12 — Spec Conformance & Missing Features | ✅ complete |
| 13 — Live Federation Compatibility | 🚧 in progress (CI-testable 13.1–13.4 done; live-interop 13.5–13.10 blocked on Phase 9 FQDN + real partner instances) |
| 14+ — Future (14–17) | 📋 planned (concrete) |

## Completed work

Phases -1, 0–12 are complete — core identity/keys/signatures, the signed client, the server
foundation + inbox/delivery, communities, proxy fallback, samples, sample Docker composition, deployment
preparation, the project & test review (suite consolidated 850 → 832), implementation gaps & usability
exploration, and spec conformance & missing features (F-01…F-30 all closed; 961 tests, 0 failures, 0
warnings). Per-slice build notes:
[changes/](changes/README.md); test tallies: [phase-notes/TEST_COUNT_HISTORY.md](phase-notes/TEST_COUNT_HISTORY.md);
substantial design calls: [decisions/](decisions/README.md).

## Remaining work

### Phase 8 — Sample Docker Composition

> Full detailed plan: [SAMPLE_PLAN.md](SAMPLE_PLAN.md). The sample becomes a one-command, self-contained
> (Docker-only routable addresses) **server + Blazor WASM server-explorer** that is the project's real-world
> interop test platform (instance→instance and instance→external).

- [x] Sample server: enable inbound signature validation (federation-ready) + register all seeded actors' keys.
- [x] Sample server: richer per-instance seed (distinct actors incl. a `manuallyApprovesFollowers` + one Ed25519 actor, reply threads, per-instance unique content).
- [x] Sample server: `samples/SampleServer/README.md` documenting implemented features with pointer information.
- [x] Blazor WASM: convert `SampleBlazorClient` to a Blazor WASM app with a DI composition root (`ExplorerSession`/`ClientService`).
- [x] Blazor WASM: log on to an instance by WebFinger address + instance switching (local + external) — address → WebFinger resolve (scheme-aware dial) → signed client; recent-instances switching ([change 073](changes/073-webfinger-resolve-instance-switching.md)).
- [x] Blazor WASM: base-URL-vs-IRI-host separation (browser dials host-published ports; IRIs carry service-name hosts) — `InstanceBaseUrls` config surface (advertised host → browser base URL) + session pre-fill + two-host test ([change 074](changes/074-base-url-vs-iri-host-config.md)).
- [x] Blazor WASM: explorer read screens (instance overview, actors directory/search, actor detail, object+replies, community) — routed pages + shared `ObjectView`; new `NodeInfo` record + `GetNodeInfoAsync`; sample seed now stores seeded notes in the object store (so object/search endpoints have data); 7 in-process tests ([change 075](changes/075-explorer-read-screens.md)).
  - [x] Blazor WASM: explorer write screens — compose (post a note / reply under a parent), follow/unfollow (local + a genuine two-instance federated follow/unfollow), and like. New `Iris.Client` `UndoFollowAsync` (Undo to the follower's own inbox) + `LikeAsync` (Like to the liker's own inbox — a content object has no inbox, only actors do); `Compose` page, `ActorDetail` follow/unfollow card, `ObjectPage` like button; 6 in-process tests ([change 076](changes/076-explorer-write-screens.md)).
   - [x] Blazor WASM: explorer **moderation write screen** — Mute/Unmute, Block/Unblock, and Flag/Unflag on the actor detail card (the S7 follow-up moderation surface). Block/flag are signed writes to the actor's own outbox (the [077] delivery model — the instance records the edge and federates to the target's inbox); mute is a local, Basic-authenticated decision (no federation). `IrisClientOptions.LocalModeration` (default on) flows the acting user's Basic auth to the client as `LocalCredentials` so `MuteAsync` works through the explorer's pre-configured client; 3 in-process tests ([change 078](changes/078-explorer-moderation-write-screen.md)).
   - [x] Blazor WASM: explorer **raw JSON inspector + proxy-fallback** — the two remaining write-surface screens, pinned in-process. The raw inspector rides `IActivityPubClient.SendAsync` (a raw request through the full signed pipeline, returning the unconsumed response — the "exact signed request + raw response" interop tool); the proxy-fallback path rides `ProxyFallbackHandler` + the server `ProxyHandler` (a direct 401 to a remote instance falls back through the home proxy, which re-signs with the acting actor's key). Full client pipeline over two in-process instances (direct 401 → A's proxy → B 200); 2 in-process tests ([change 079](changes/079-explorer-raw-inspector-and-proxy-fallback.md)).
- [x] **Delivery-model fix — the outbox is the write surface.** Invariant ([SAMPLE_PLAN §4.3a](SAMPLE_PLAN.md), [ARCHITECTURE.md](reference/ARCHITECTURE.md)): an actor's **outbox** is the write surface for the activities it authors — the client POSTs an authored activity to the actor's *own* outbox (never a recipient's inbox); the server records it in the outbox + activity store and is the **only** thing that delivers to a recipient's inbox (server→server). Done: (1) a **POST-outbox write surface** on the server (`OutboxPublishHandler`), (2) handlers that record the local edge keying off the acting actor and server-deliver to the resolved recipient, (3) the **client routing every authored write (Follow/Block/Flag/Undo/Like/Post/Reply) to the actor's own outbox**, and (4) **updating every test** that asserted the old `…/inbox` delivery targets (inbound federation — a remote peer sending to this actor — still uses `…/inbox`); the actor's home follow edge is now recorded regardless of target locality ([change 077](changes/077-delivery-model-outbox-write-surface.md)).
- [x] Blazor WASM: Dockerfile (multi-stage → static host) + `iris-ui` compose service (host:8090, routable as `iris-ui`). Multi-stage `samples/SampleBlazorClient/Dockerfile` (SDK → `aspnet` static-file host) publishes the WASM site + the dedicated `samples/IrisStaticHost` (a minimal plain ASP.NET Core `UseStaticFiles` host on `8090` — a separate project because the Blazor WASM SDK pins the browser-wasm target); `iris-ui` joins `iris-a`/`iris-b` on `iris-net` (host `8090:8090`, TCP-connect health check). Verified: image builds + serves `index.html`/`blazor.webassembly.js`/`app.css`/a `.wasm` (all 200) + SPA fallback on `8090`; 883 tests green ([change 080](changes/080-wasm-dockerfile-and-iris-ui-compose-service.md)).
- [x] Smoke test: UI reachability over `iris-net` + signed cross-container federation (a→b Follow + Accept) + proxy fallback; keep opt-in gate. Five checks over genuine sockets: per-instance WebFinger, cross-container WebFinger (a→b), `iris-ui` index 200, a **signed** Follow from iris-a's alice to iris-b's alice (202 + the federated edge recorded on iris-b — validated by resolving alice's key from iris-a's actor document via the new `FederatedActorDocumentFetcher`), and the proxy fallback (iris-a relays a GET to iris-b, 200). The signed request is driven by the new `tools/IrisSigner` helper (curl cannot produce an ActivityPub HTTP signature); the acting key is dumped to the container locally via the opt-in `Iris__DumpKeyTo` env var ([change 081](changes/081-s10-signed-federation-proxy-smoke-test.md)).
- [x] Docs: `samples/SampleBlazorClient/README.md` (explorer + external-instance mechanism, no real dev FQDN committed) + `DEPLOYMENT.md` 3-service topology. New `samples/SampleBlazorClient/README.md` documents the explorer (screens → `IActivityPubClient` calls, logon + the base-URL/IRI-host rule, the external-instance mechanism, testing, and a manual-exploration checklist); `DEPLOYMENT.md`'s smoke-test section + the "real follow/post federation" note updated to the post-S10 signed-federation story ([change 082](changes/082-s11-sample-blazor-readme-deployment-update.md)).

### Phase 11 — Implementation Gaps & Usability Exploration
- [x] Finalize the gap register and prioritize the remaining usability issues. The gap register (G-1…G-6 + H-4) was refreshed to its verified implementation status ([change 084](changes/084-phase11-operator-reject-gap-register.md)); the J-1…J-22 usability-friction register is in [PHASE_11_USER_JOURNEYS.md](reference/PHASE_11_USER_JOURNEYS.md).
- [x] Walk the main user flows end-to-end and document the friction points for discovery, posting, moderation, and error handling. [PHASE_11_USER_JOURNEYS.md](reference/PHASE_11_USER_JOURNEYS.md) (J-1…J-22, research-only, 11.2).
- [x] Extend end-to-end tests for realistic failure cases and user journeys. Client failure modes end-to-end over the real signed pipeline: 404 not-found is a final answer (`GetObjectAsync`/`GetActorAsync` → `null`, no retry) and a direct 401 falls back through the home instance's proxy (`ProxyFallbackHandler` outermost, Basic-auth POST to `/ap/v1/proxy/{target}`); server-side bad-signature (401), unknown actor (401/404), proxy allowlist (403), and rate-limit (429) are covered by the signature-middleware + proxy-fallback integration tests ([change 083](changes/083-phase11-5-client-failure-mode-e2e-tests.md)).
- [x] Confirm the remaining implementation gaps are covered by regression tests.
  - [x] The **operator `Reject` path** for `manuallyApprovesFollowers` actors (the live outbound half of the gate — gap G-2's Reject / J-10) is implemented + covered: a Basic-authenticated `POST /ap/v1/u/{handle}/follows/{followId}` (the same credential seam as the mute/relay endpoints) builds `FollowIris.BuildReject`, records it in the activity store + the actor's outbox, removes the provisional follow edge, and server-delivers the `Reject` back to the follower's inbox (signed as the local actor) so the remote removes its edge. Regression tests: a single-instance endpoint suite (auth, status codes 401/404/400/409/403/410/202, idempotent re-reject, local-follower guard) + a two-instance federation loop proving the Reject is delivered back over the wire and the follower's edge is removed. The gap register (G-1/G-2/G-3/G-4/G-5/G-6 + H-4) was also refreshed to its verified implementation status ([change 084](changes/084-phase11-operator-reject-gap-register.md)).
- [x] Ensure failure modes such as bad signatures, unknown actors, 404s, rate limits, and proxy fallback are exercised in realistic paths.
  - [x] Client failure modes end-to-end over the real signed pipeline: 404 not-found is a final answer (`GetObjectAsync`/`GetActorAsync` → `null`, no retry) and a direct 401 falls back through the home instance's proxy (`ProxyFallbackHandler` outermost, Basic-auth POST to `/ap/v1/proxy/{target}`); server-side bad-signature (401), unknown actor (401/404), proxy allowlist (403), and rate-limit (429) are already covered by the signature-middleware + proxy-fallback integration tests ([change 083](changes/083-phase11-5-client-failure-mode-e2e-tests.md)).

### Phase 12 — Spec Conformance & Missing Features
- [x] G-3: community outbox write surface for outbound community follow (`POST /ap/v1/c/{name}/outbox`, [change 085](changes/085-phase12-community-outbox-g3.md)).
- [x] G-1 residual: outbox-publish `Create` full fan-out to all remote followers (`RecordCreateLocalAsync` → `IEnumerable<Iri>`, [change 086](changes/086-phase12-g1-residual-outbox-create-fanout.md)).
- [x] F-15: outbox-publish `Announce` full fan-out to all remote followers (`OutboxPublishHandler` `Announce` branch + shared `GetRemoteNonBlockedFollowersAsync`, [change 087](changes/087-phase12-f15-outbox-announce-fanout.md)).
- [x] F-19: typed `DeliveryResult` for all client write operations (`DeliverAsync` + convenience methods return `Task<DeliveryResult>` instead of `Task<int>`, [change 088](changes/088-phase12-f19-typed-delivery-result.md)).
- [x] F-16: community membership primitives `Offer`/`Invite`/`Join`/`Leave` (`MembershipActivityHandler` + specificity-based `InboxProcessor` dispatch; `AddRemoveActivityHandler` split into exact-type `AddActivityHandler`/`RemoveActivityHandler`, [change 089](changes/089-phase12-f16-membership-primitives.md)).
- [x] F-18: unordered `Collection` support in the client's collection enumeration (`FetchCollectionPageAsync` accepts a base `Collection`, guarded by `is not OrderedCollection`, [change 090](changes/090-phase12-f18-unordered-collection.md)).
- [x] F-17: intransitive activity handlers `Read`/`View`/`Listen`/`Travel`/`Arrive` (`IntransitiveActivityHandler` registered before `MembershipActivityHandler`, forwards non-intransitive activities, [change 091](changes/091-phase12-f17-intransitive-activities.md)).
- [x] F-23: `?q=` content filter on the community feed endpoint (`ICommunityFeedService.GetFeedAsync` gained an optional `query` that delegates to `SearchCommunityAsync`, [change 092](changes/092-phase12-f23-feed-filter.md)).
- [x] F-29: canonical `url` on served content objects (the object-document endpoint sets the object's own IRI as the `url` when absent, preserving an author-provided `url`, [change 093](changes/093-phase12-f29-canonical-url.md)).
- [x] F-30: WebFinger two paths — regression tests for the bare RFC 8615 path (the two-path situation is deliberate, Decision #10 / C-01; no code change needed, [change 094](changes/094-phase12-f30-webfinger-two-paths.md)).
- [x] Finish the remaining lower-priority conformance gaps (F-01 through F-30 all closed; F-26/F-27/F-28/F-31 deferred to Phase 13 as Mastodon-specific or spec-valid-as-is, change 094).
- [x] Keep the regression suite green for signatures, pagination, WebFinger, NodeInfo, and object handling (961 tests, 0 failures, 0 warnings).
- [x] Confirm the outstanding compatibility edge cases against the current feature inventory (F-26–F-31 reviewed; all are Phase 13 or spec-valid-as-is).
- [x] Record any remaining spec deviations and planned follow-up work (F-26 Question/poll, F-27 custom emoji, F-28 `sensitive` flag, F-31 `ld+json` production — all documented as deliberate deferrals to Phase 13).
- [x] Prioritize the remaining feature gaps by spec requirement, interop impact, and effort (Wave 1–4 prioritization in MISSING_FEATURES.md).
- [x] Keep the implementation aligned with the ActivityPub and ActivityStreams rules already captured in the missing-features inventory.

### Phase 13 — Live Federation Compatibility
- [x] 13.1: Mastodon extension passthrough tests — prove the "don't drop unknown properties" guarantee (F-01) holds for the Mastodon surface (`sensitive`, `toot:emoji`, `poll`, actor extensions), [change 097](changes/097-phase13-1-mastodon-extension-passthrough.md).
- [x] 13.2: `ld+json` accept behavior — regression tests proving the inbox accepts `Content-Type: application/ld+json` (Decision #4's accept half), [change 098](changes/098-phase13-2-ld-json-accept.md).
- [x] 13.3: Mastodon `Question`/poll inbound handling — regression tests proving a signed `Create` carrying a poll-bearing `Note` (Mastodon's poll shape) is accepted, stored, and served with the full poll shape preserved verbatim; a `Question`-typed object is not tested (library quirk — `Question` is an activity, not an object), [change 099](changes/099-phase13-3-poll-inbound.md).
- [x] 13.4: Mastodon `sensitive` flag inbound handling — regression tests proving the `sensitive` boolean is forwarded as an opaque property (on both a `Note` and an embedded `Image`) over the signed inbox pipeline, [change 100](changes/100-phase13-4-sensitive-inbound.md).
- [ ] 13.5: Stand up the public instance and the dev partner instances (Mastodon, Lemmy, Threads).
- [ ] 13.6: Verify interoperability with Mastodon, Lemmy, Threads, and the planned compatibility targets.
- [ ] 13.7: Record live findings, deviations, and required follow-up fixes.
- [x] 13.8: Decide the CI gating/opt-in model for the live interop suite — implemented the runtime gate (`LiveInteropOptions` + `LiveGuard.TryRequires` + `Iris.LiveInterop.Tests` project, opt-in via `IRIS_LIVE_INTEROP=1` + FQDN config; default `dotnet test` stays green), [change 101](changes/101-phase13-8-ci-gating-model.md).
- [ ] 13.9: Run real-user enumeration against target instances via WebFinger, NodeInfo, and search-style discovery.
- [ ] 13.10: Assert server-to-external-server compatibility across signatures, content types, pagination, delivery, and error handling.

### Phase 14+ — Future work

> Phase 14+ is the post-Phase-13 work: the live-interop execution and gap remediation, then the
> production-readiness phases (auth hardening, real persistence, observability). These are **to be
> expanded into concrete slices** as Phase 13 live-interop results come in — the gap register
> ([RISK_GAP_REGISTER.md](reference/RISK_GAP_REGISTER.md)) and compatibility matrix
> ([COMPATIBILITY_MATRIX.md](reference/COMPATIBILITY_MATRIX.md)) are the sources of truth for what
> needs fixing.

#### Phase 14 — Live-Interop Execution & Gap Remediation
- [ ] 14.1: Run the live-interop suite (13.5–13.10) against Mastodon, Lemmy, Pleroma, and Threads (the `Iris.LiveInterop.Tests` scenario stubs are the payload — fill in the per-platform admin-API adapters).
- [ ] 14.2: Record live findings (PASS / GAP / MISMATCH per matrix scenario) and update the gap register.
- [ ] 14.3: Fix the confirmed gaps (the six predicted gaps in COMPATIBILITY_MATRIX.md §5 + any MISMATCH findings), each with a regression test.
- [ ] 14.4: Re-run the live suite to confirm the fixes; iterate until all matrix scenarios are PASS or the gap is Accepted (v1 limitation).

#### Phase 15 — Authentication Hardening
- [x] 15.1: Replace Basic auth with OAuth2/Bearer tokens for client authentication (the `IActorCredentialValidator` seam is the swap point).
- [ ] 15.2: Implement the OAuth2 authorization code + PKCE flow for the Blazor WASM client (the `BasicAuthClientAuthenticator` is the current implementation to replace).
- [ ] 15.3: Add token refresh + revocation support.
- [ ] 15.4: Update the sample + deployment docs for the new auth flow.

#### Phase 16 — Production Persistence & Scaling
- [ ] 16.1: Implement a database-backed persistence provider (PostgreSQL/SQLite) replacing the in-memory stores (the `IPersistenceProvider` seam is the swap point).
- [ ] 16.2: Add distributed cache support (Redis) for the remote-actor/key caches (the `ICache<T>` seam).
- [ ] 16.3: Add shared-inbox / relay support for larger-scale delivery (the F-01/F-06 relay infrastructure is the foundation).
- [ ] 16.4: Multi-instance scaling (load balancing, session affinity, the delivery queue's distributed backpressure).

#### Phase 17 — Observability & Transport Hardening
- [ ] 17.1: Structured logging + OpenTelemetry metrics (request rate, delivery success rate, cache hit rate, signature validation failures).
- [ ] 17.2: Circuit breaker + retry policy hardening for outbound delivery (the `DeliveryRetryOptions` is the foundation).
- [ ] 17.3: Rate limiting on inbound endpoints (the existing 429 path is the seam).
- [ ] 17.4: Health-check endpoints + graceful shutdown.

## Near-term priorities

1. Build the Phase 8 sample per [SAMPLE_PLAN.md](SAMPLE_PLAN.md): **S1 done** (federation-ready server +
   rich seed, [change 070](changes/070-sample-federation-ready.md)), **S2 done** (server README),
   **S3 done** (Blazor WASM server-explorer scaffold + `ExplorerSession`/`AddIrisExplorer` DI + app
   shell + `WebFingerAddress` + 17 in-process tests, [change 072](changes/072-sample-blazor-wasm-explorer.md)),
    **S4 done** (logon by WebFinger *resolve* + instance switching — scheme-aware dial + `actorIriOverride`
    + 4 in-process tests, [change 073](changes/073-webfinger-resolve-instance-switching.md)),
     **S5 done** (base-URL-vs-IRI-host separation — `InstanceBaseUrls` config surface + session pre-fill
     + two-host test, [change 074](changes/074-base-url-vs-iri-host-config.md)),
      **S6 done** (explorer read screens — instance/actors/actor-detail/object+replies/community pages +
      shared `ObjectView` + `NodeInfo`/`GetNodeInfoAsync` client surface + object-store seed + 7 in-process
      tests, [change 075](changes/075-explorer-read-screens.md)),
       **S7 (core) done** (explorer write screens — compose (post/reply), follow/unfollow (local + a genuine
       two-instance federated follow/unfollow), and like; new `UndoFollowAsync`/`LikeAsync` client surface +
       `Compose`/follow/like pages + 6 in-process tests, [change 076](changes/076-explorer-write-screens.md);
        the moderation / raw-JSON / proxy-fallback write screens are follow-up slices) →
        **delivery-model fix done** (the outbox is the write surface: the client POSTs an authored activity to
        the actor's *own* outbox; the server records it + server-delivers to the recipient's inbox — POST-outbox
        write surface, handlers key off the acting actor, the client routes every authored write to the actor's
        outbox, and every test asserting the old `…/inbox` delivery targets is updated; the actor's home follow
        edge is recorded regardless of target locality, [change 077](changes/077-delivery-model-outbox-write-surface.md))
        → **moderation write screen done** (mute/block/unblock/flag/unflag on the actor detail card —
        block/flag are signed outbox writes, mute is a local Basic-auth decision; `IrisClientOptions.
        LocalModeration` flows the acting user's Basic auth to the client as `LocalCredentials`,
        [change 078](changes/078-explorer-moderation-write-screen.md))
        → **raw JSON inspector + proxy-fallback done** (the raw inspector rides `SendAsync` — a raw signed
        request returned unconsumed; the proxy-fallback path rides `ProxyFallbackHandler` + the server
        `ProxyHandler` — a direct 401 falls back through the home proxy, which re-signs with the acting
        actor's key; full client pipeline over two in-process instances,
        [change 079](changes/079-explorer-raw-inspector-and-proxy-fallback.md))
        → external-instance → 3-service compose + smoke path.
2. ~~Finish the remaining Phase 10 doc-sync (ARCHITECTURE / PROJECTS / TESTING / CODING_STYLE).~~ (Phase 10 complete.)
3. ~~Close the remaining gaps in Phase 12 and keep the conformance suite passing.~~ (Phase 12 complete — F-01…F-30 all closed, 961 tests, 0 failures, 0 warnings.)
4. Phase 13 live-interop (13.5–13.10): stand up the public instance + dev partner instances (Mastodon, Lemmy, Threads) and verify interoperability — **blocked on Phase 9 FQDN/TLS + real partner instances**. The CI-testable sub-slices (13.1–13.4: Mastodon extension passthrough, `ld+json` accept, poll inbound, `sensitive` inbound) are done (961→974, changes 097–100).

## Summary

The project already has a strong base: core identity, client/server federation, communities, proxying, deployment planning, implementation gaps, and spec conformance are all complete (Phases 0–12). The next milestone is Phase 13 live-interop: proving real-world interoperability against external ActivityPub implementations (Mastodon, Lemmy, Threads) — blocked on Phase 9 FQDN/TLS + real partner instances. The CI-testable Phase 13 sub-slices (13.1–13.4) are done (974 tests, 0 failures, 0 warnings).
