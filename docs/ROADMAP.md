# Iris — Roadmap (Compressed)

This is the shortened working roadmap. The detailed phase notes and design evidence remain in [ROADMAP.md](ROADMAP.md), [CHANGELOG.md](CHANGELOG.md), and the docs under [decisions/](decisions/README.md).

## Status at a glance

| Phase | Status |
|---|---|
| -1 — Project Reorganization | 📋 planned |
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
| 10 — Project & Test Review | 🚧 in progress |
| 11 — Implementation Gaps & Usability Exploration | 🚧 in progress |
| 12 — Spec Conformance & Missing Features | 🚧 in progress |
| 13 — Live Federation Compatibility | 📋 planned |
| 14+ — Future | 📋 abstract |

## Completed work (short summary)

- Phase -1 — project reorganization not yet started; planned as the structural cleanup phase.
- Phase 0 — baseline solution, project structure, shared packages, test harness, and green build/test setup.
- Phase 1 — identity, keys, HTTP-signature support, JSON-LD handling, and cache primitives.
- Phase 2 — signed ActivityPub client, discovery, WebFinger, retries, collections, and client caching.
- Phase 3 — server foundation, actor/docs endpoints, in-memory persistence, versioning, and caching.
- Phase 4 — inbound validation, inbox processing, delivery worker, follow lifecycle, and federated propagation.
- Phase 5 — communities, membership/follow flows, unified feed, and community capability support.
- Phase 6 — proxy fallback and signed proxy delivery.
- Phase 7 — sample server/client composition and end-to-end federation validation.
- Phase 9 — deployment planning, TLS/FQDN runbook, compatibility matrix, and interop risk register.

## Remaining work

### Phase -1 — Project Reorganization
- [ ] Review the current project and solution layout for structural drift.
- [ ] Organize source files into clearer folder structures within each project.
- [ ] Group related features by domain such as actors, delivery, inbox processing, discovery, persistence, and server endpoints.
- [ ] Reconcile test project organization so helpers, fixtures, and shared test utilities are easier to find and maintain.
- [ ] Consolidate duplicate helper code and naming patterns before deeper work builds on the current structure.
- [ ] Keep the reorganization low-risk by moving code without changing behavior.

### Phase 8 — Project & Test Review
- [ ] Audit the suite for redundant, duplicate, or low-value tests.
- [ ] Remove or merge tests that add no signal or duplicate existing coverage.
- [ ] Consolidate repeated test setup, seeding, and host helpers into shared utilities.
- [ ] Keep the remaining tests focused on real behavior and regression protection.
- [ ] Review dead fixtures and over-mocked tests; keep only tests that prove real behavior.
- [ ] Add final changelog/test-count records for the consolidation work.

### Phase 10 — Sample Docker Composition
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

1. Finish the remaining documentation and review tasks in Phase 10.
2. Close the remaining gaps in Phase 12 and keep the conformance suite passing.
3. Stand up the live federation test instances for Phase 13.
4. Use live interop results to update the gap register and roadmap priorities.

## Summary

The project already has a strong base: core identity, client/server federation, communities, proxying, and deployment planning are all in place. The next milestones are to finish the remaining specification and review work, then prove real-world interoperability against external ActivityPub implementations.
