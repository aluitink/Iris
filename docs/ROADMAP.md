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
- [ ] Finish the Blazor WASM host for the sample client.
- [ ] Complete the Dockerfile for the sample Blazor client and serve it through a static host.
- [ ] Complete the remaining Docker smoke-path and CI wiring.
- [ ] Validate the compose topology remains stable for local and CI runs.
- [ ] Keep the smoke script as an opt-in guard when Docker is unavailable.

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

1. Finish the remaining Phase 10 doc-sync (ARCHITECTURE / PROJECTS / TESTING / CODING_STYLE).
2. Close the remaining gaps in Phase 12 and keep the conformance suite passing.
3. Stand up the live federation test instances for Phase 13.
4. Use live interop results to update the gap register and roadmap priorities.

## Summary

The project already has a strong base: core identity, client/server federation, communities, proxying, and deployment planning are all in place. The next milestones are to finish the remaining specification and review work, then prove real-world interoperability against external ActivityPub implementations.
