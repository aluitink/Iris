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
| 11 — Implementation Gaps & Usability Exploration | 🚧 in progress |
| 12 — Spec Conformance & Missing Features | 🚧 in progress |
| 13 — Live Federation Compatibility | 📋 planned |
| 14+ — Future | 📋 abstract |

## Completed work

Phases -1, 0–9, and 10 are complete — core identity/keys/signatures, the signed client, the server
foundation + inbox/delivery, communities, proxy fallback, samples, sample Docker composition, deployment
preparation, and the project & test review (suite consolidated 850 → 832). Per-slice build notes:
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
- [ ] Finalize the gap register and prioritize the remaining usability issues.
- [ ] Walk the main user flows end-to-end and document the friction points for discovery, posting, moderation, and error handling.
- [ ] Extend end-to-end tests for realistic failure cases and user journeys.
- [ ] Confirm the remaining implementation gaps are covered by regression tests.
  - [x] The **operator `Reject` path** for `manuallyApprovesFollowers` actors (the live outbound half of the gate — gap G-2's Reject / J-10) is implemented + covered: a Basic-authenticated `POST /ap/v1/u/{handle}/follows/{followId}` (the same credential seam as the mute/relay endpoints) builds `FollowIris.BuildReject`, records it in the activity store + the actor's outbox, removes the provisional follow edge, and server-delivers the `Reject` back to the follower's inbox (signed as the local actor) so the remote removes its edge. Regression tests: a single-instance endpoint suite (auth, status codes 401/404/400/409/403/410/202, idempotent re-reject, local-follower guard) + a two-instance federation loop proving the Reject is delivered back over the wire and the follower's edge is removed. The gap register (G-1/G-2/G-3/G-4/G-5/G-6 + H-4) was also refreshed to its verified implementation status ([change 084](changes/084-phase11-operator-reject-gap-register.md)).
- [ ] Ensure failure modes such as bad signatures, unknown actors, 404s, rate limits, and proxy fallback are exercised in realistic paths.
  - [x] Client failure modes end-to-end over the real signed pipeline: 404 not-found is a final answer (`GetObjectAsync`/`GetActorAsync` → `null`, no retry) and a direct 401 falls back through the home instance's proxy (`ProxyFallbackHandler` outermost, Basic-auth POST to `/ap/v1/proxy/{target}`); server-side bad-signature (401), unknown actor (401/404), proxy allowlist (403), and rate-limit (429) are already covered by the signature-middleware + proxy-fallback integration tests ([change 083](changes/083-phase11-5-client-failure-mode-e2e-tests.md)).

### Phase 12 — Spec Conformance & Missing Features
- [x] G-3: community outbox write surface for outbound community follow (`POST /ap/v1/c/{name}/outbox`, [change 085](changes/085-phase12-community-outbox-g3.md)).
- [x] G-1 residual: outbox-publish `Create` full fan-out to all remote followers (`RecordCreateLocalAsync` → `IEnumerable<Iri>`, [change 086](changes/086-phase12-g1-residual-outbox-create-fanout.md)).
- [ ] Finish the remaining lower-priority conformance gaps.
- [ ] Keep the regression suite green for signatures, pagination, WebFinger, NodeInfo, and object handling.
- [ ] Confirm the outstanding compatibility edge cases against the current feature inventory.
- [ ] Record any remaining spec deviations and planned follow-up work.
- [ ] Prioritize the remaining feature gaps by spec requirement, interop impact, and effort.
- [ ] Keep the implementation aligned with the ActivityPub and ActivityStreams rules already captured in the missing-features inventory.

### Phase 13 — Live Federation Compatibility
- [ ] Stand up the public instance and the dev partner instances.
- [ ] Verify interoperability with Mastodon, Lemmy, Threads, and the planned compatibility targets.
- [ ] Record live findings, deviations, and required follow-up fixes.
- [ ] Decide the CI gating/opt-in model for the live interop suite.
- [ ] Run real-user enumeration against target instances via WebFinger, NodeInfo, and search-style discovery.
- [ ] Assert server-to-external-server compatibility across signatures, content types, pagination, delivery, and error handling.

### Phase 14+ — Future work
- [ ] Replace Basic auth with OAuth2/Bearer tokens or a dedicated key-exchange flow.
- [ ] Implement real persistence and distributed cache support.
- [ ] Add shared inbox / relay support, moderation features, and larger-scale delivery.
- [ ] Add the next round of observability, transport hardening, and multi-instance scaling work.

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
2. Finish the remaining Phase 10 doc-sync (ARCHITECTURE / PROJECTS / TESTING / CODING_STYLE).
3. Close the remaining gaps in Phase 12 and keep the conformance suite passing.
4. Use the sample (instance→instance + instance→external via dev FQDNs) to feed Phase 13 live-interop results
   back into the gap register and roadmap priorities.

## Summary

The project already has a strong base: core identity, client/server federation, communities, proxying, and deployment planning are all in place. The next milestones are to finish the remaining specification and review work, then prove real-world interoperability against external ActivityPub implementations.
