# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing applications (Blazor clients, ASP.NET Core servers, or any .NET app).

This document is the **index**. The full plan is split across the files below — read them in order for the complete picture.

## Documentation

| File | Contents |
|---|---|
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns (caching, HTTP signatures, key model, proxy fallback), spec research |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details: `Iris.Core`, `Iris.Client`, `Iris.Server`, `Iris.Server.InMemory`, `Iris.Client.Extensions` |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy, multi-instance `TestServer` harness, test project layout, deferred Mastodon live test |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Phased plan (Phase 0–14+) — brief waypoints only |
| [docs/phase-notes/README.md](docs/phase-notes/README.md) | **Implementation history**: per-phase/slice build notes and test-count tracking |
| [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md) | **Binding** coding conventions — C# style, naming, error handling, async, and the rules for working with 3rd-party `KristofferStrube.ActivityStreams` types |
| [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) | Operating instructions for the autonomous dev loop (one turn at a time), incl. the doc-maintenance rules |
| [docs/decisions/](docs/decisions/) | Design-decision documents (one file per substantial decision), linked from phase notes/status summaries |

## The Short Version

- **Clean and simple** — small, focused abstractions; no framework lock-in beyond .NET itself.
- **One client, two directions** — a single `net10.0` client library used by both client apps (Blazor) and servers (server-to-server).
- **Server as extension** — ActivityPub server capability is added to an existing ASP.NET Core app via `IServiceCollection`/`IApplicationBuilder` extensions.
- **ActivityStreams via `KristofferStrube.ActivityStreams`** — the existing NuGet package provides all ActivityStream/ActivityPub type definitions and JSON-LD serialization. `Iris.Core` does NOT re-implement the object model; it adds Iris-specific concerns (signing, identity, IRI helpers, validation) on top. See [Coding Style — 3rd-Party Types](docs/reference/CODING_STYLE.md#3rd-party-activitystreams-types).
- **Actor-keyed client auth** — the client authenticates to our server (Basic auth in v1), fetches the actor document with the private key, and signs subsequent requests with that key.
- **Layered caching** — client and server cache fetched objects with short TTLs; every cached read has a `bypassCache` / `forceRefresh` escape hatch.
- **Community-aware** — first-class `Group` actors as communities (Lemmy-style) with unified feed/collection APIs.
- **Versioned API surface** — route-prefix versioning (`/ap/v1/...`) from day one; `Iris-Version` meta header; `iris:capabilities` for feature discovery.
- **Integration-first testing** — end-to-end tests against multiple in-process server instances with distinct hostnames, not a sprawl of unit tests.

## Solution Layout

```
Iris.slnx
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Client.Extensions/     net10.0 — DI/extensions for client app integration
│   ├── Iris.Server/                net10.0 — ASP.NET Core extensions, endpoints, middleware, community feeds
│   └── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
├── tests/
│   ├── Iris.Testing/               shared multi-instance TestServer harness
│   ├── Iris.Core.Tests/
│   ├── Iris.Client.Tests/
│   ├── Iris.Client.Extensions.Tests/
│   ├── Iris.Server.Tests/
│   ├── SampleServer.Tests/
│   └── SampleBlazorClient.Tests/
└── samples/
    ├── SampleServer/               minimal ASP.NET Core app hosting Iris.Server
    └── SampleBlazorClient/         Blazor WebAssembly app using Iris.Client
```

## Conventions (summary)

- **TFM: net10.0** for all projects. C# latest, nullable enabled, file-scoped namespaces.
- **`System.Text.Json` exclusively.** ActivityStream/ActivityPub types come from `KristofferStrube.ActivityStreams` — we do NOT re-implement them.
- **Central package management** (`Directory.Packages.props`).
- **Dependency direction**: `Iris.Core` → `KristofferStrube.ActivityStreams` + BCL; `Iris.Client` → `Iris.Core`; `Iris.Server` → `Iris.Core` + `Iris.Client` + ASP.NET Core; `Iris.Server.InMemory` → `Iris.Server`.
- **Caching**: all cached reads expose a `bypassCache` / `forceRefresh` parameter. No cached path is opaque.
- **Versioning**: route prefix (`/ap/v1/...`) is authoritative; `Iris-Version` header is meta; new capabilities via `iris:`-namespaced terms (configurable namespace base).
- **Testing**: integration-first (xUnit + multi-instance `TestServer` harness); unit tests reserved for pure logic.

> **The full conventions, including the binding rules for 3rd-party ActivityStreams types, are in [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md).**

## Keeping the docs lean

This file is the **index** — it must stay small. The rules for where information belongs:

- **PLAN.md** (this file): index, conventions summary, status table, short carried-forward list. Nothing else grows over time.
- **ROADMAP.md**: phases as brief waypoints — checkbox bullets, one line each. No build notes, no rationale.
- **docs/phase-notes/**: append-only implementation history — per-phase/slice build notes and test-count tracking.
- **docs/decisions/**: one document per *substantial* design decision (trade-offs, alternatives, spec references); phase notes/status summaries link to it.
- **When in doubt, link instead of copy.** A pointer beats a duplicated paragraph.

The full rules, including the per-turn workflow, are in [docs/reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](docs/reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).

## Current Status

Phases 0–9 are complete.

Current focus is **Phase 10+**, with implementation details tracked in the table below and full per-slice notes in [docs/phase-notes/README.md](docs/phase-notes/README.md).

Highlights since the Phase 10 review began:

- **Phase 10 (slices 1.1–1.7):** major refactors + test-harness consolidation (`CachingReadThrough<T>`, `FollowResponseActivityHandler<T>`, shared endpoint cores, `forceRefresh` → `bypassCache`, shared `Iris.Testing` helpers, shared `ActivityPubHostFactory`) with 444→478 tests (+34).
- **Phase 11 (slices 11.1–11.10):** usability/write-path gaps closed (`Discovery`, `FollowAsync`, `PostNoteAsync`, local/remote `Create` flow), plus RSA-2048 + PEM actor keys, `Undo` (un-follow), and `manuallyApprovesFollowers` support.
- **Phase 12 (in progress, slices 12.1–12.8):** Wave 1 is complete (`sharedInbox`, `Update`, `Delete`, `Tombstone`, Ed25519, `Move`) and the conformance suite is in place; Wave 2 has started with `Like` and followed-feed/home-timeline.
- **Decisions resolved in this stretch:** #44, #45, #46, #47, #48, #49, #50, #51, #52, #53.
- **Test count progression in this stretch:** 444→653.

Use the phase table below as the concise status ledger; use phase notes/decisions for rationale and implementation depth.

| Phase | Status | One-line summary |
|---|---|---|
| 0 — Scaffolding | ✅ complete | Solution, projects (net10.0), central package management, multi-instance `TestServer` harness; build + tests green. |
| 1 — Core: Identity, Keys, Signatures & Caching | ✅ complete | `Iri`, identity/key foundation, HTTP-signature layer, caching layer — all unit-tested. |
| 2 — Client Library | ✅ complete | Signing pipeline, WebFinger discovery, client caching, rich paged collections, retry/content-negotiation handlers. |
| 3 — Server Foundation | ✅ complete | Persistence seam + in-memory impl, `/ap/v1` endpoints (actor doc w/ owner-only `privateKey`, WebFinger, NodeInfo), server caching + `Cache-Control`. |
| 4 — Inbox & Delivery | ✅ complete | Inbound signature validation, inbox processor + activity handlers, outbound delivery queue/worker, two-sided Follow/Accept/Reject lifecycle, per-actor delivery signing, cached remote fetches. |
| 5 — Community / Group Support | ✅ complete | Community store (membership + follows), community endpoints (doc/members/inbox/feed/search), unified feed, community following, `iris:capabilities`. |
| 6 — Proxy Fallback | ✅ complete | Server proxy endpoint + policy stack (allowlist, rate limit), client `ProxyFallbackHandler`. |
| 7 — Blazor Client Extensions & Samples | ✅ complete | `Iris.Client.Extensions` package + `SampleServer` + `SampleBlazorClient` (console composition root) + E2E tests (login → signed feed → proxy fallback); `Iris.Client` page-1 collection fix. WASM host deferred to Phase 8. |
| 8 — Sample Docker Composition | 🚧 in progress | `SampleServer` multi-stage Dockerfile + `.dockerignore`; two-instance compose (`iris-a`/`iris-b` on `iris-net`, routable hostnames); health checks + smoke-test script (verified: both healthy, cross-container WebFinger 200); `UseKestrel()` fix so the server starts. Blazor-client Dockerfile (WASM host) + CI job deferred. |
| 9 — Real-World Deployment Preparation | ✅ complete | All five deliverables done, grounded in the real config/client/server/test surface: FQDN & TLS plan + bootstrap runbook (`docs/reference/DEPLOYMENT_PREP.md`), real-user enumeration design (`docs/reference/ENUMERATION_DESIGN.md`), compatibility matrix (`docs/reference/COMPATIBILITY_MATRIX.md`), test-harness extension design (`docs/reference/INTEROP_TEST_HARNESS.md`, Decision #41), risk & gap register (`docs/reference/RISK_GAP_REGISTER.md`); resolves OQ #2 + #3. Prep only — no live tests (Phase 13). |
| 10 — Project & Test Review | 🚧 in progress | Slices 1.1–1.7 done (444→478, 34 new tests). 1.1: dead-code sweep + IRI dedup. 1.2: CollectionPage → Core. 1.3: cache engines → CachingReadThrough. 1.4: Accept/Reject → FollowResponseActivityHandler. 1.5: inbox + community-collection → shared cores. 1.6: API surface pass (forceRefresh → bypassCache). 1.7: test-harness consolidation (per-test federation/seeding/JSON helpers → `Iris.Testing` `TestSeeder`/`Jwk`/`JsonDoc`; then the 12 per-test `StartServer` helpers → shared `ActivityPubHostFactory`, ~334 lines removed). Remaining: doc sync (ARCHITECTURE/PROJECTS/TESTING/CODING_STYLE). |
| 11 — Implementation Gaps & Usability Exploration | 🚧 in progress | End-to-end user journeys + realistic integration coverage. 11.1 fixed client/server page-1 collection interop (`OrderedCollection.next` via `ExtensionData`; `ClientServerCollectionInteropTests`, 478→482). 11.2 was research-only (`docs/reference/PHASE_11_USER_JOURNEYS.md`, J-1…J-22). 11.3 added bundle discovery (`Discovery` + `ResolveActorAsync`, 482→486). 11.4 added `client.FollowAsync` (486→490). 11.5 added `client.PostNoteAsync` (490→491). 11.6 surfaced local posts by recording inbound `Create` to the author's outbox (491→500). 11.7 federated local posts to remote followers (500→507). 11.8 switched default keys to RSA-2048 + `publicKeyPem` + PKCS#1 RSA public-PEM import (507→513, Decision #44). 11.9 added `Undo` un-follow handling (`UndoActivityHandler`, 513→524, Decision #45). 11.10 honored `manuallyApprovesFollowers` (record edge, suppress auto-`Accept`, operator `Accept`/`Reject`; doc flag + live `BuildReject`, 524→536, Decision #46). Remaining: Phase 12. |
| 12 — Spec Conformance & Missing Features | 🚧 in progress | Spec audit + prioritized gap closure + conformance testing. 12.1 (research-only) produced `docs/reference/MISSING_FEATURES.md` (baseline, F-01…F-31, C-01…C-08, four-wave plan). 12.2 closed F-01 `sharedInbox` (advertise + consume with per-actor fallback, 536→542, Decision #47). 12.3 closed F-02 `Update`, F-03 `Delete`, F-10 `Tombstone` (store embedded Create object, update in place for local actor, tombstone on delete, add `GET /ap/v1/o/{**path}`, 542→557, Decision #48). 12.4 closed F-05 Ed25519 by introducing `ISigningKey` and wiring signing/verification + key resolution/loading to support `Ed25519Key` (`BouncyCastle.Cryptography`), 557→606 (+24 tests), Decision #49. 12.5 closed F-08 `Move` (`MoveActivityHandler` re-points local follow edges old→new and invalidates outbound remote caches, 587→602 (+15 tests), Decision #50). 12.6 added conformance coverage (`ConformanceSuiteTests`, `OutboundSignatureConformanceTests`) and fixed WebFinger media type to `application/jrd+json`, 602→611 (+9 tests), Decision #51. Wave 1 + conformance suite closed. 12.7 closed F-04 `Like` (local like-edge store + liked collection + community recording path; directed semantics, 611→624, Decision #52). 12.8 closed F-14 followed feed/home timeline (`IFollowFeedService`/`FeedService`, local+remote follow union, dedup/cap, endpoint + client API, 624→653, Decision #53). Remaining: Wave 2/3/4 gaps (next: F-12 `tag`/`attachment`/`inReplyTo`, F-13 global search, F-22 delivery retry). |
| 13 — Live Federation Compatibility | ⏸ deferred | Run the public instance against real Mastodon/Lemmy/Threads; real-user enumeration. Blocked on the Phase 9 FQDN. |
| 14+ — Future | 📋 abstract | Auth upgrade, real persistence, delivery at scale, shared inbox, observability, 1.0 API review. |

**Carried forward:** (a) spec-research findings not yet captured/folded back; (b) a bare `dotnet build` in the root is blocked by MSB1011 (stray root scratch files — delete `Program.cs`/`inspect.csproj`/`packages.lock.json` manually).

Track progress in [docs/ROADMAP.md](docs/ROADMAP.md).
