# Iris — Roadmap

> Part of the [Iris plan](../PLAN.md). See also [Architecture](ARCHITECTURE.md), [Projects](PROJECTS.md), [Testing](TESTING.md), [Coding Style](CODING_STYLE.md).
>
> This file keeps only **brief waypoints**. All completed-work detail (per-slice build notes, test counts, resolved design decisions) lives in the [Changelog](CHANGELOG.md); substantial design decisions get their own document in [decisions/](decisions/).
>
> **Maintenance rules:** a phase entry is a short description + one-line checkbox bullets. If a bullet needs a paragraph, the paragraph goes in the Changelog or a decision doc — not here. Resolved decisions are recorded in the Changelog's "Resolved Decisions" section (this file only keeps *open* questions). Full rules: [AUTONOMOUS_LOOP.md — Keeping the docs lean](AUTONOMOUS_LOOP.md#keeping-the-docs-lean).

## Status at a glance

| Phase | Status |
|---|---|
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
| 11 — Implementation Gaps & Usability Exploration | 📋 planned |
| 12 — Spec Conformance & Missing Features | 📋 planned |
| 13 — Live Federation Compatibility | ⏸ deferred |
| 14+ — Future | 📋 abstract |

## Completed phases (detail in [CHANGELOG.md](CHANGELOG.md))

- **Phase 0 — Scaffolding.** Solution + all projects (TFM **net10.0**), central package management, xUnit test projects with the shared `Iris.Testing` multi-instance `TestServer` harness; build + tests green.
- **Phase 1 — Core.** `Iri` + `IriExtensions`; identity/key foundation (`KeyPair`, `KeyPairGenerator`, `IKeyStore`); HTTP-signature layer (both profiles, RSA + ECDSA); `ActivityJson`; caching layer (`ICache<T>`/`MemoryCache<T>`, TTL, LRU, stale-while-revalidate). All unit-tested.
- **Phase 2 — Client Library.** `IActivityPubClient` + factory with the `Retry → JsonLd → Signing` pipeline; Basic-auth authenticator; WebFinger discovery; client caches; rich paged collections. 69 unit + 7 integration tests.
- **Phase 3 — Server Foundation.** Persistence seam + in-memory impl; `/ap/v1` endpoints (actor doc with owner-only `privateKey`, WebFinger, NodeInfo) + `Iris-Version` header; server caching + response `Cache-Control`. 27 tests.
- **Phase 4 — Inbox & Delivery.** Inbound signature validation + key resolution; inbox processor + activity-handler pipeline; outbound delivery queue/worker; two-sided Follow/Accept/Reject lifecycle; per-actor delivery signing; cached remote actor-doc/WebFinger/collection fetches; paged local collection endpoints; `Announce` propagation. 109 Server tests, incl. two-instance federation loops.
- **Phase 5 — Community / Group Support.** `ICommunityStore` (membership + follows); community endpoints (document, members, inbox, feed, search); unified feed; community following (incl. community-follows-community); `iris:capabilities`. 172 Server tests.
- **Phase 6 — Proxy Fallback.** Server `POST /ap/v1/proxy/{target}` (signed forward, allowlist + rate limiting); client `ProxyFallbackHandler` (401/403 → proxy retry). 177 Server + 96 Client tests.

## Phase 7 — Blazor Client Extensions & Samples 🚧

- [x] `Iris.Client.Extensions` package (BCL-only composition root, Decision #38).
- [x] `IrisSession` (identity selection, in-memory key persistence for session lifetime).
- [x] Pre-configured pipeline with proxy fallback (`IrisClientFactory` + `IrisClientBuilder`; the `AddIrisClient(services, …)` DI sugar is intentionally out of scope — Decision #38).
- [x] `SampleServer` app (ASP.NET Core + Iris.Server + in-memory persistence + Basic auth + a sample community; 9 integration tests).
- [x] `SampleBlazorClient` app (a console composition root mirroring `SampleServer`; injectable transport so the same composition runs against a real server or an in-process `TestServer`. A WASM host is deferred — the composition is the Blazor `Program.cs` equivalent).
- [x] End-to-end: client authenticates → gets key → signs requests → community feed → proxy fallback to remote server (4 integration tests, incl. a two-instance proxy allowlist + relay test).
- [ ] WASM host for `SampleBlazorClient` (a Blazor WASM app rendering the composition; deferred to Phase 8 with the Docker topology).

## Phase 8 — Sample Docker Composition 📋

> Prove the Phase 7 samples deploy and interoperate as a **real system**, not just in-process `TestServer` instances.

- [x] Dockerfile for `SampleServer` (multi-stage SDK → aspnet runtime; `samples/SampleServer/Dockerfile` + root `.dockerignore`).
- [ ] Dockerfile for `SampleBlazorClient` (WASM, served by a static host) — deferred with the WASM host.
- [x] `docker-compose.yml` wiring **two `SampleServer` instances** (`iris-a`, `iris-b`) on an internal network (`iris-net`) with routable service-name hostnames; each advertises its own base URI.
- [x] A second `SampleServer` instance in the compose file (distinct hostname) to exercise **real cross-container federation** over genuine network I/O — not in-process.
- [x] Health-check probes (TCP connect to `:8080`); a smoke-test script (`scripts/docker-smoke-test.sh`) that boots the stack, waits for health, and asserts cross-container WebFinger reachability. The Blazor-client auth + federated-post assertion is deferred with the WASM host (the in-process two-instance integration tests cover the client path).
- [x] Document the compose topology, hostname assignments, and how to run/tear down locally (`docs/DEPLOYMENT.md`).
- [ ] Wire as an opt-in CI job (Docker available); skip in local/dev runs without Docker — deferred until a baseline CI workflow exists; the smoke script's opt-in gate (skip when Docker is unavailable) is the interim measure.

## Phase 9 — Real-World Deployment Preparation ✅

> Ideation + preparation only. **No live tests run yet** — we cannot exercise real instances until the operator provides a public, routable FQDN. This phase produces the artifacts and plans that make Phase 13 runnable. **All five deliverables are done** (FQDN/TLS plan + bootstrap runbook, real-user enumeration design, compatibility matrix, test-harness extension, risk & gap register), all grounded in the real config/client/server/test surface.

- [x] **FQDN & TLS plan**: document the operator-provided FQDN requirements (DNS, A/AAAA record, TLS certificate provisioning, reverse-proxy config) and the exact config surface Iris needs to bind to it (`docs/DEPLOYMENT_PREP.md` §1; resolves OQ #3 → Decision #40).
- [x] **Instance bootstrap runbook**: step-by-step to stand up a public Iris instance (key generation, actor creation, community setup, NodeInfo/WebFinger publication) against the FQDN (`docs/DEPLOYMENT_PREP.md` §2, grounded in `ActivityPubServerOptions` + the `SampleServer` seed).
- [x] **Real-user enumeration design**: plan for discovering and enumerating real users/communities on other instances (WebFinger lookups, NodeInfo discovery, directory/search endpoints) — the read-only reconnaissance we'll run once the FQDN is live (`docs/ENUMERATION_DESIGN.md`, grounded in the real client surface: `IWebFingerResolver` + `GetActorAsync` + `GetCollectionItemsAsync` + `IriExtensions.*Of`; NodeInfo/directory/search via `SendAsync`).
- [x] **Compatibility matrix**: define the target ecosystems (Mastodon, Threads, Lemmy, Pleroma, and others) and, for each, the concrete interop scenarios to verify (follow, post, receive, community/group, search, pagination, content types, signatures) (`docs/COMPATIBILITY_MATRIX.md`, grounded in the real capability map; predicts 6 concrete gaps for Phase 13 to confirm).
- [x] **Test harness extension**: design the opt-in live-interop suite structure (gated by env flag + FQDN config) so Phase 13 is a matter of filling in targets, not building the harness (`docs/INTEROP_TEST_HARNESS.md`; Decision #41 — separate runtime-gated project `Iris.LiveInterop.Tests` + hoisted `Iris.Testing` harness; live = in-process + a real `HttpClientHandler` transport).
- [x] **Risk & gap register**: capture known unknowns (Threads' non-standard AP surface, Lemmy's group semantics, rate limits, moderation) that live testing must resolve (`docs/RISK_GAP_REGISTER.md` — synthesizes the 6 predicted gaps from the compatibility matrix + the Threads/Lemmy platform unknowns + operational + harness risks, with Phase 13 entry criteria).

## Phase 10 — Project & Test Review 📋

> A full, deliberate pass over the codebase and test suite to remove redundancy and consolidate before deeper work builds on top.

- [ ] **Test audit**: identify redundant, duplicate, or low-value tests (tests that assert the same behavior as a broader integration test, over-mocked unit tests that add no signal, dead fixtures). Remove or merge them. *(Slice 1.1: audit complete — suite well-organized; removed the dead `LazyActorDocumentFetcher` fixture. Slice 1.7: consolidated the ~9 copy-pasted per-test federation/seeding/collection-JSON helpers into `Iris.Testing` (`TestSeeder` + `Jwk` + `JsonDoc`) with 12 dedicated unit tests — ~597 lines of duplication removed across 12 test files. Slice 1.7b: consolidated the 12 near-identical per-test `StartServer` helpers into a single `Iris.Testing.ActivityPubHostFactory` (+ `ActivityPubHostOptions` / `IdentityKeys`) that hosts the real pipeline and captures the union of the key-store/fetcher/delivery-transport/credential-validator/proxy/extra-service seams — ~334 lines of duplication removed across 11 test files; the harness bridge is now complete.)*
- [ ] **Code consolidation**: find duplicated logic across `Iris.Core`/`Iris.Client`/`Iris.Server` (e.g. parallel cache engines, repeated IRI/JSON helpers, near-identical handler stacks) and consolidate into shared abstractions. *(Slice 1.1: IRI-resolution dedup. Slice 1.2: CollectionPage + page-flatten + collection-IRI dedup. Slice 1.3: parallel cache engine consolidation. Slice 1.4: Accept/RejectActivityHandler → FollowResponseActivityHandler<T> base (~80 lines removed). Slice 1.5: inbox POST + community-collection endpoint handlers → shared cores (~120 lines removed). Slice 1.7b: harness consolidation — the 12 per-test `StartServer` helpers → shared `ActivityPubHostFactory` (~334 lines removed). Code + harness consolidation complete.)*
- [x] **Dead-code sweep**: remove unused types, parameters, and configuration surface that accumulated across phases. *(Slice 1.1: removed the fully-dead `CommunityIris.cs` (0 callers) + `LazyActorDocumentFetcher.cs` (0 callers); swept the public-type surface for other zero-caller types — only these two were dead.)*
- [x] **API surface pass**: review public API for consistency (naming, parameter conventions like `bypassCache`/`forceRefresh`, disposal contracts) ahead of the 1.0 stabilization. *(Slice 1.6: audited all 135 public types / 447 members across Core/Client/Server. Found + fixed the one inconsistency: `forceRefresh` → `bypassCache` in 10 Server public signatures (the Client + Core engine already used `bypassCache`). All other categories clean: CancellationToken placement, XML docs, disposal contracts, Async/I-prefix/boolean naming, Iri boundary.)*
- [ ] **Documentation sync**: ensure `ARCHITECTURE.md`, `PROJECTS.md`, `TESTING.md`, and `CODING_STYLE.md` reflect the post-review reality.
- [ ] Record test-count deltas and consolidation decisions in the [Changelog](CHANGELOG.md).

## Phase 11 — Implementation Gaps & Usability Exploration 🚧

> Walk every feature **end-to-end as a user** to find where the journey breaks, then close those gaps with more complete integration tests.

- [x] **User-journey walkthroughs**: for each capability (auth → key → sign → post → follow → community feed → proxy fallback → cross-instance delivery), trace the full path a real user/app would take and note every gap, dead-end, or confusing step. **Done (Slice 11.2)** — see [PHASE_11_USER_JOURNEYS.md](PHASE_11_USER_JOURNEYS.md): all eight capabilities walked end-to-end; the capability gaps G-1…G-6 are re-derived from the user's view (as J-18/J-10/J-11/J-15/J-4/J-8) and a new usability-friction register (J-1…J-22) is recorded — headline: the *write* path (post/follow/follow-a-community, J-6/J-9/J-21) is not reachable through the client as a user would drive it. (Since then, **J-21** [Slice 11.3] and **J-9** [Slice 11.4] are resolved; the remaining write-path dead-end is **J-6** — no client "post" API.)
- [ ] **Gap register**: catalog implementation gaps found (missing endpoints, unhandled activity types, incomplete error paths, missing config) with severity and a fix plan. *(The Slice 11.2 walkthrough produces the J-1…J-22 register in [PHASE_11_USER_JOURNEYS.md](PHASE_11_USER_JOURNEYS.md); this bullet is the step of folding those findings back into a prioritised, severity-ranked register with a per-gap fix plan.)*
- [ ] **Usability questions**: answer "if I were a user, how do I get from X to Y?" for the core flows; document friction points (discovery, error messages, required config, mental model).
- [ ] **Integration test expansion**: for each gap and journey, add or extend integration tests (multi-instance `TestServer`) that prove the end-to-end path now works — not just the happy path, but the realistic sequences a user performs.
- [ ] **Error-path coverage**: ensure failure modes (bad signature, unknown actor, 404, rate-limit, proxy fallback trigger) are exercised end-to-end, not just in isolation.
- [x] Close the carried-forward **client/server page-1 interop gap** (Slice 11.1): the server's page-1 `OrderedCollection` now carries a `next` pointer (via `ExtensionData`, since the ActivityStreams type has no typed `next`) when more pages remain, and the client reads it to walk past page 1. Proven end-to-end by `ClientServerCollectionInteropTests` (real signed client over a live in-process server, multi-page outbox).
- [x] Close **J-21 — the client's discovery service was not exposed in the bundle** (Slice 11.3): the handle→IRI step (`@user@host` → actor IRI) was a dead-end. `IrisClientBundle` now exposes `Discovery` (`IDiscoveryService`) + a `ResolveActorAsync(account, ct)` convenience; `IrisClientBuilder.Build()` builds a default WebFinger-backed service (plain unsigned `HttpClient`, reusing the bundle's WebFinger cache) and `WithDiscovery(...)` supplies a custom one. Proven by 4 unit tests (exposure/override/delegation/unknown-handle) + 1 e2e test resolving a handle through the real server's `/.well-known/webfinger` (482→486).
- [x] Close **J-9 — the client had no "follow" API** (Slice 11.4): following required hand-building a `Follow` activity and knowing the target inbox IRI. `IActivityPubClient.FollowAsync(Iri actorId, Iri targetId, ct)` builds the `Follow` (deterministic `Id` = `{actor}/follows/{target}`, `Actor`/`Object` as `Link`s), derives the target's inbox (`targetId.InboxOf()`), and delivers it through the signed pipeline. Proven by 3 unit tests (inbox/actor/object/id, serialization, delivery-status passthrough) + 1 e2e test (`FollowIntegrationTests`): a local actor authenticates and follows a second seeded actor over a live in-process server; the signed follow is validated (single-instance self-fetch of the follower's actor doc via a deferred `LazyHandler`) and the follow edge is recorded (486→490).

## Phase 12 — Spec Conformance & Missing Features 📋

> A focused audit against the ActivityPub/ActivityStreams specifications to confirm conformance and surface missing features before going live.

- [ ] **Spec conformance audit**: systematically check the implementation against the ActivityPub spec (signing, delivery, WebFinger, NodeInfo, collections, object/activity types) and the ActivityStreams 2.0 data model; record every deviation.
- [ ] **Missing-feature inventory**: identify spec-mandated or widely-expected features not yet implemented (e.g. `Undo`, `Delete`, `Update` activity handling, `sharedInbox`, `relay`, `tag`/`attachment` handling, `ordered` vs `unordered` collections, `manuallyApprovesFollowers`).
- [ ] **Prioritized fix plan**: rank conformance gaps and missing features by (a) spec-mandated vs nice-to-have, (b) impact on real-world interop, (c) effort.
- [ ] **Implement high-priority gaps**: close the spec-mandated and interop-critical items; defer the rest to Phase 14+.
- [ ] **Conformance test suite**: add integration tests that assert spec-required behaviors (wire format, headers, status codes, pagination semantics) so conformance is regression-protected.
- [ ] Fold the carried-forward **spec-research findings** (from Phase 0) into this audit's output.

## Phase 13 — Live Federation Compatibility ⏸ (deferred — requires the Phase 9 FQDN)

> The ultimate interop proof: run our public Iris instance against real external federated servers. **Blocked on the operator-provided FQDN from Phase 9.**

- [ ] Stand up the public Iris instance per the Phase 9 bootstrap runbook.
- [ ] **Mastodon**: follow a Mastodon account → receive & store its posts; post from Iris → confirm Mastodon receives it. Orchestrate via Mastodon's admin/REST API (accounts, posts, follows).
- [ ] **Lemmy**: follow a Lemmy community/user → receive posts; post from Iris → confirm delivery. Verify group/community semantics.
- [ ] **Threads** (and other targets from the Phase 9 compatibility matrix): verify the scenarios defined there; document any non-standard AP surface encountered.
- [ ] **Real-user enumeration**: run the Phase 9 reconnaissance — discover and enumerate real users/communities on target instances via WebFinger/NodeInfo/search.
- [ ] Assert server-to-external-server compatibility across all targets (signatures, content types, pagination, WebFinger, delivery, error handling).
- [ ] Wire as a dedicated, opt-in CI job (gated by FQDN + env flag); skip in local/dev runs.
- [ ] Record findings (conformances, deviations, workarounds) in the [Changelog](CHANGELOG.md) and feed back into Phase 12's gap register.

## Phase 14+ (abstract, to be expanded later)

- **Auth upgrade**: replace Basic auth with OAuth2 bearer tokens or a dedicated key-exchange endpoint. `IActorCredentialValidator` makes this a drop-in swap.
- **Real persistence**: EF Core / PostgreSQL implementation of `IPersistenceProvider` (including `ICommunityStore`).
- **Delivery at scale**: background queue (RabbitMQ/Redis), parallel delivery, fan-out for large follower sets.
- **SharedInbox / Relay** support.
- **Community features**: moderation (hide/remove posts), community tags/subscriptions, cross-community search, community-level blocking.
- **Caching at scale**: distributed cache (Redis) implementation of `ICache<T>` for multi-instance server deployments.
- **Transport security hardening**: key rotation, `keyDocuments`, multi-key actors, key expiry.
- **Observability**: OpenTelemetry spans for delivery, metrics for inbox throughput, cache hit-rate dashboards.
- **API surface review**: stabilize `Iris.Core`/`Iris.Client` for 1.0.

## Carried-forward items

- **Spec research pass** (Phase 0): the research directive is in [ARCHITECTURE.md](ARCHITECTURE.md#spec-research); concrete findings are not yet captured/folded back.
- **Bare root build** (Phase 0): a bare `dotnet build` in the repo root is blocked by MSB1011 — stray root scratch files (`Program.cs`/`inspect.csproj`/`packages.lock.json`) need manual deletion to restore it.

> Resolved in Phase 11 (Slice 11.1): the **client/server page-1 interop gap** (Phase 5) is closed — the server's page-1 `OrderedCollection` now carries a `next` pointer and the client walks past page 1 (see [CHANGELOG.md](CHANGELOG.md)).

## Open Questions (to resolve as we go)

1. **Route prefix shape**: confirm the exact prefix convention (e.g. `/ap/v1/...` vs `/v1/ap/...`) and whether the unversioned root (`/ap/...`) should alias to the latest version for convenience/back-compat.
2. **Compatibility matrix scope** (Phase 9): which ecosystems are in-scope for Phase 13 live testing (Mastodon, Threads, Lemmy, Pleroma, others) and the priority ordering; Threads' non-standard ActivityPub surface may need special handling or deferral.
3. **Live-interop CI gating** (Phase 13): how the opt-in live suite is gated in CI (env flag + FQDN secret) and how often it runs (on-demand vs. scheduled), given it depends on external, mutable third-party instances.

> Resolved (moved to [CHANGELOG.md](CHANGELOG.md) "Resolved Decisions"): #2 Sample Docker topology (→ #39) and #3 FQDN & TLS provisioning (→ #40).
