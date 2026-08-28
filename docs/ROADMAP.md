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
| 11 — Implementation Gaps & Usability Exploration | 🚧 in progress |
| 12 — Spec Conformance & Missing Features | 🚧 in progress |
| 13 — Live Federation Compatibility | 📋 planned (FQDNs allocated) |
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

- [x] **User-journey walkthroughs** (Slice 11.2): all eight capabilities walked end-to-end as a user — see [PHASE_11_USER_JOURNEYS.md](PHASE_11_USER_JOURNEYS.md). Headline: the *write* path (post/follow/follow-a-community) was not reachable through the client as a user would drive it; the J-1…J-22 usability-friction register is recorded there. Since then **J-21** (11.3), **J-9** (11.4), **J-6's client half** (11.5), **J-8 / J-6's "recorded" server half** (11.6), **J-18** (11.7 — outbound `Create` to the author's remote followers), and **J-10** (11.10 — `manuallyApprovesFollowers` gates auto-accept) are resolved — a local post is now surfaced *and* federated, and an operator can gate a follow.
- [ ] **Gap register**: catalog implementation gaps found (missing endpoints, unhandled activity types, incomplete error paths, missing config) with severity and a fix plan. *(The Slice 11.2 walkthrough produces the J-1…J-22 register in [PHASE_11_USER_JOURNEYS.md](PHASE_11_USER_JOURNEYS.md); this bullet is the step of folding those findings back into a prioritised, severity-ranked register with a per-gap fix plan.)*
- [ ] **Usability questions**: answer "if I were a user, how do I get from X to Y?" for the core flows; document friction points (discovery, error messages, required config, mental model).
- [ ] **Integration test expansion**: for each gap and journey, add or extend integration tests (multi-instance `TestServer`) that prove the end-to-end path now works — not just the happy path, but the realistic sequences a user performs.
- [ ] **Error-path coverage**: ensure failure modes (bad signature, unknown actor, 404, rate-limit, proxy fallback trigger) are exercised end-to-end, not just in isolation.
- [x] Close the carried-forward **client/server page-1 interop gap** (Slice 11.1): the server's page-1 `OrderedCollection` now carries a `next` pointer (via `ExtensionData`) when more pages remain, and the client walks past page 1. Proven by `ClientServerCollectionInteropTests` (real signed client over a live in-process server, multi-page outbox).
- [x] Close **J-21 — the client's discovery service was not exposed in the bundle** (Slice 11.3): the handle→IRI step (`@user@host` → actor IRI) was a dead-end. `IrisClientBundle` now exposes `Discovery` (`IDiscoveryService`) + `ResolveActorAsync(account, ct)`; `IrisClientBuilder.Build()` builds a default WebFinger-backed service (reusing the bundle's WebFinger cache) and `WithDiscovery(...)` supplies a custom one. 4 unit + 1 e2e tests (482→486).
- [x] Close **J-9 — the client had no "follow" API** (Slice 11.4): `IActivityPubClient.FollowAsync(Iri actorId, Iri targetId, ct)` builds the `Follow` (deterministic `Id`, `Actor`/`Object` as `Link`s), derives the target's inbox (`targetId.InboxOf()`), and delivers it through the signed pipeline. 3 unit + 1 e2e tests (486→490).
- [x] Close **J-6 (client half) — the client had no "post a note" API** (Slice 11.5): `IActivityPubClient.PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, ct)` builds the `Create` (embedded `Note`, deterministic ids from a content hash so a retried post dedupes, optional audience `to`) and delivers it to `actorId.InboxOf()` (the "local post" path) through the signed pipeline. 3 unit + 1 e2e tests (490→491). **Remaining (server half):** record the note in the author's outbox (J-8, Slice 11.6 — done) + outbound `Create` to followers (J-18, Phase 12).
- [x] Close **J-8 — the server did not surface a local post in the author's outbox** (Slice 11.6, J-6's server half): an inbound `Create` was stored but never surfaced. A dedicated `CreateActivityHandler : ActivityHandlerBase<Create>` now records the `Create` in the recipient's outbox when the recipient is a local **person** (the author's own post), and in the community's local members' outboxes when the recipient is a local **community** (delegating to the shared `CommunityContentRecorder`); the `InboxProcessor`'s most-specific-handler preference lets it intercept every inbound `Create` before the catch-all `CommunityInboxActivityHandler`. 8 unit + 1 e2e tests (491→500). **Remaining (J-18, Phase 12):** outbound `Create` to the author's remote followers, so the post federates.
- [x] **Actor keys: default to RSA + serve a PEM public key** (Slice 11.8): the default generated actor key is now **RSA-2048** (`KeyPairGenerator.Generate`/`GenerateRsa`, signed `RSA-SHA256` — EC P-256 remains available via `GenerateEcP256`), and the actor document serves the public key as **PEM** (`publicKeyPem` in the `publicKey` extension, from `KeyPair.ExportPublicKeyPem()`) instead of a JWK. `KeyPair.FromPem` now also imports a **PKCS#1 RSA public-key PEM** (`-----BEGIN RSA PUBLIC KEY-----` — the raw `RsaPublicKey` DER, wrapped by hand into a SubjectPublicKeyInfo envelope since .NET 10 has no `ImportPkcs1PublicKey`) as a verify-only key, and `RemoteInboundKeyResolver.AlgorithmFromPem` short-circuits to RSA on that label, so a server publishing only a PKCS#1 PEM (a common OpenSSL default, e.g. Rayven) is a resolvable federation peer. 6 new tests (507→513); Resolved Decision #44. Live-interop confirmation against the Phase 13 targets remains.
- [x] **Handle an un-follow: the `Undo` activity (the follow inverse)** (Slice 11.9): a `Follow` had no inverse — a user could follow but not un-follow. A new `UndoActivityHandler : ActivityHandlerBase<Undo>` handles the ActivityStreams `Undo` primitive (it undoes the activity referenced by its `object`). An `Undo` of a `Follow` is delivered to the **follower's** inbox (the party that made the follow), so the handler treats `InboxDelivery.RecipientIri` as the follower (the same convention `AcceptActivityHandler`/`RejectActivityHandler` use for follow responses), resolves the follow's target from the original `Follow` (fetched from the local activity store), and, when the recipient is a **local** actor, removes the `follower → target` edge — a local **person** via `IFollowStore.RemoveFollowAsync`, a local **community** via `ICommunityStore.RemoveFollowAsync` (the inverse of `FollowActivityHandler`'s community branch). A missing/unknown/non-`Follow` object is a no-op; a remote recipient is not this instance's concern. `FollowIris` gains `UndoIri` (`{actor}/undoes/{followId}`) + `BuildUndo` so a future client `UnfollowAsync` can reference the same deterministic `Follow` IRI `FollowAsync` builds. 11 new tests (513→524); Resolved Decision #45. **Remaining:** the follow's *outbox* entry is not removed (outbox removal is an open design question — Phase 12).
- [x] **Honor `manuallyApprovesFollowers` — don't auto-accept a follow (J-10)** (Slice 11.10): the follow lifecycle was auto-accept only (`FollowIris.BuildReject` had no caller), so an operator could not gate a follow. `ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName` names the library-untyped `manuallyApprovesFollowers` actor property (carried in `ExtensionData`, seeded by the host). `FollowActivityHandler` now reads it: a **local person** with it set has the follow **edge recorded** (the follower's content reaches the local followers' outboxes) but the `Accept` **suppressed** — the operator responds with an explicit `Accept`/`Reject`; a **community** always auto-accepts and a missing/`false` value is the default. `BuildActorDocument` echoes the flag on the public document only when `true`, so a remote follower can see it. `BuildReject` now has a live path (operator signs a `Reject` → the existing `RejectActivityHandler` removes the edge). 12 new tests (524→536); Resolved Decision #46. **Remaining:** a dedicated moderation surface (list pending follows, one-click approve/reject) is a Phase 12 item.

## Phase 12 — Spec Conformance & Missing Features 📋

> A focused audit against the ActivityPub/ActivityStreams specifications to confirm conformance and surface missing features before going live.

- [x] **Spec conformance audit**: systematically check the implementation against the ActivityPub spec (signing, delivery, WebFinger, NodeInfo, collections, object/activity types) and the ActivityStreams 2.0 data model; record every deviation. *(Slice 12.1: the full audit is in [MISSING_FEATURES.md](MISSING_FEATURES.md) §2 (the implemented surface) + §3 (conformance notes C-01…C-08).)*
- [x] **Missing-feature inventory**: identify spec-mandated or widely-expected features not yet implemented (e.g. `Undo`, `Delete`, `Update` activity handling, `sharedInbox`, `relay`, `tag`/`attachment` handling, `ordered` vs `unordered` collections, `manuallyApprovesFollowers`). *(Slice 12.1: [MISSING_FEATURES.md](MISSING_FEATURES.md) §2 — F-01…F-31, severity-ranked, spec-mandated items marked ★. Note: `Undo` (Slice 11.9) and `manuallyApprovesFollowers` (Slice 11.10) are already implemented; the inventory lists what remains — `Update`/`Delete`/`Like`/`Move`/`Add`/`Remove`, `sharedInbox`, `relay`, EdDSA, moderation, the followed feed, etc.)*
- [x] **Prioritized fix plan**: rank conformance gaps and missing features by (a) spec-mandated vs nice-to-have, (b) impact on real-world interop, (c) effort. *(Slice 12.1: [MISSING_FEATURES.md](MISSING_FEATURES.md) §4 — four waves; Wave 1 (spec-mandated & interop-critical) = `sharedInbox`, EdDSA, `Update`, `Delete`, `Move`.)*
- [ ] **Implement high-priority gaps**: close the spec-mandated and interop-critical items; defer the rest to Phase 14+. *(Remaining: Wave 1 first — `sharedInbox` (F-01), EdDSA (F-05), `Update` (F-02), `Delete` (F-03), `Move` (F-08).)*
- [ ] **Conformance test suite**: add integration tests that assert spec-required behaviors (wire format, headers, status codes, pagination semantics) so conformance is regression-protected. *(Remaining: per the per-item test list in [MISSING_FEATURES.md](MISSING_FEATURES.md) §4.)*
- [x] Fold the carried-forward **spec-research findings** (from Phase 0) into this audit's output. *(Slice 12.1: [MISSING_FEATURES.md](MISSING_FEATURES.md) §5 — the inventory is the fold-back of the Phase 0 spec-research directive.)*

## Phase 13 — Live Federation Compatibility 📋 (FQDNs allocated — ready to stand up)

> The ultimate interop proof: run our public Iris instance against real external federated servers. **The FQDNs are now allocated** (below) — this phase is unblocked; the remaining prerequisite is TLS + the reverse-proxy config from the Phase 9 runbook.

**Allocated FQDNs & internal ports** (use these for all real-world testing):

| Instance | FQDN | Internal port | Role |
|---|---|---|---|
| **Beta production** | `iris.luit.ink` | `8088` | The public instance other servers federate with. |
| **Dev 1** | `iris-dev1.luit.ink` | `8081` | Dev/test instance (cross-instance federation partner). |
| **Dev 2** | `iris-dev2.luit.ink` | `8082` | Dev/test instance (cross-instance federation partner). |

> **Public endpoint = `https://<FQDN>` on 443 — no port in the URL.** The reverse proxy terminates TLS on `:443` and maps to the instance's **internal** port by hostname: `iris.luit.ink` → `:8088`, `iris-dev1.luit.ink` → `:8081`, `iris-dev2.luit.ink` → `:8082`. The advertised `BaseUri` is `https://<FQDN>` (no port); the internal port is the Kestrel bind address the proxy forwards to, and never appears in a public IRI.

- [ ] Stand up the public Iris instance (`iris.luit.ink`, internal `:8088`) per the Phase 9 bootstrap runbook; stand up the two dev instances (`iris-dev1.luit.ink`, internal `:8081`; `iris-dev2.luit.ink`, internal `:8082`) as cross-instance federation partners. All three are reached publicly as `https://<FQDN>` on 443 (the proxy maps 443 → the internal port by hostname).
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
