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
| 8 — Sample Docker Composition | 🚧 in progress |
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
- [ ] Blazor WASM: explorer read screens (instance overview, actors directory/search, actor detail, object+replies, community).
- [ ] Blazor WASM: explorer write screens (post/reply, cross-instance follow, like, moderation) + raw JSON inspector + proxy-fallback screen.
- [ ] Blazor WASM: Dockerfile (multi-stage → static host) + `iris-ui` compose service (host:8090, routable as `iris-ui`).
- [ ] Smoke test: UI reachability over `iris-net` + signed cross-container federation (a→b Follow + Accept) + proxy fallback; keep opt-in gate.
- [ ] Docs: `samples/SampleBlazorClient/README.md` (explorer + external-instance mechanism, no real dev FQDN committed) + `DEPLOYMENT.md` 3-service topology.

### Phase 11 — Implementation Gaps & Usability Exploration
- [ ] Finalize the gap register and prioritize the remaining usability issues.
- [ ] Walk the main user flows end-to-end and document the friction points for discovery, posting, moderation, and error handling.
- [ ] Extend end-to-end tests for realistic failure cases and user journeys.
- [ ] Confirm the remaining implementation gaps are covered by regression tests.
- [ ] Ensure failure modes such as bad signatures, unknown actors, 404s, rate limits, and proxy fallback are exercised in realistic paths.

### Phase 12 — Spec Conformance & Missing Features
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
    + two-host test, [change 074](changes/074-base-url-vs-iri-host-config.md)) →
    explorer read/write screens + external-instance → 3-service
    compose + smoke path.
2. Finish the remaining Phase 10 doc-sync (ARCHITECTURE / PROJECTS / TESTING / CODING_STYLE).
3. Close the remaining gaps in Phase 12 and keep the conformance suite passing.
4. Use the sample (instance→instance + instance→external via dev FQDNs) to feed Phase 13 live-interop results
   back into the gap register and roadmap priorities.

## Summary

The project already has a strong base: core identity, client/server federation, communities, proxying, and deployment planning are all in place. The next milestones are to finish the remaining specification and review work, then prove real-world interoperability against external ActivityPub implementations.
