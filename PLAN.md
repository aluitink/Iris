# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing applications (Blazor clients, ASP.NET Core servers, or any .NET app).

This document is the **index**. The full plan is split across the files below — read them in order for the complete picture.

## Documentation

| File | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns (caching, HTTP signatures, key model, proxy fallback), spec research |
| [docs/PROJECTS.md](docs/PROJECTS.md) | Per-project details: `Iris.Core`, `Iris.Client`, `Iris.Server`, `Iris.Server.InMemory`, `Iris.Client.Extensions` |
| [docs/TESTING.md](docs/TESTING.md) | Integration-first testing strategy, multi-instance `TestServer` harness, test project layout, deferred Mastodon live test |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Phased plan (Phase 0–14+) — brief waypoints only |
| [docs/CHANGELOG.md](docs/CHANGELOG.md) | **History** of completed work: per-slice build notes, test counts, resolved design decisions |
| [docs/CODING_STYLE.md](docs/CODING_STYLE.md) | **Binding** coding conventions — C# style, naming, error handling, async, and the rules for working with 3rd-party `KristofferStrube.ActivityStreams` types |
| [docs/AUTONOMOUS_LOOP.md](docs/AUTONOMOUS_LOOP.md) | Operating instructions for the autonomous dev loop (one turn at a time), incl. the doc-maintenance rules |
| [docs/decisions/](docs/decisions/) | Design-decision documents (one file per substantial decision), linked from the changelog |

## The Short Version

- **Clean and simple** — small, focused abstractions; no framework lock-in beyond .NET itself.
- **One client, two directions** — a single `net10.0` client library used by both client apps (Blazor) and servers (server-to-server).
- **Server as extension** — ActivityPub server capability is added to an existing ASP.NET Core app via `IServiceCollection`/`IApplicationBuilder` extensions.
- **ActivityStreams via `KristofferStrube.ActivityStreams`** — the existing NuGet package provides all ActivityStream/ActivityPub type definitions and JSON-LD serialization. `Iris.Core` does NOT re-implement the object model; it adds Iris-specific concerns (signing, identity, IRI helpers, validation) on top. See [Coding Style — 3rd-Party Types](docs/CODING_STYLE.md#3rd-party-activitystreams-types).
- **Actor-keyed client auth** — the client authenticates to our server (Basic auth in v1), fetches the actor document with the private key, and signs subsequent requests with that key.
- **Layered caching** — client and server cache fetched objects with short TTLs; every cached read has a `bypassCache` / `forceRefresh` escape hatch.
- **Community-aware** — first-class `Group` actors as communities (Lemmy-style) with unified feed/collection APIs.
- **Versioned API surface** — route-prefix versioning (`/ap/v1/...`) from day one; `Iris-Version` meta header; `iris:capabilities` for feature discovery.
- **Integration-first testing** — end-to-end tests against multiple in-process server instances with distinct hostnames, not a sprawl of unit tests.

## Solution Layout

```
Iris.sln
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Server/                net10.0 — ASP.NET Core extensions, endpoints, middleware, community feeds
│   └── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
├── tests/
│   ├── Iris.Testing/               shared multi-instance TestServer harness
│   ├── Iris.Core.Tests/
│   ├── Iris.Client.Tests/
│   └── Iris.Server.Tests/
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

> **The full conventions, including the binding rules for 3rd-party ActivityStreams types, are in [docs/CODING_STYLE.md](docs/CODING_STYLE.md).**

## Keeping the docs lean

This file is the **index** — it must stay small. The rules for where information belongs:

- **PLAN.md** (this file): index, conventions summary, status table, short carried-forward list. Nothing else grows over time.
- **ROADMAP.md**: phases as brief waypoints — checkbox bullets, one line each. No build notes, no rationale.
- **CHANGELOG.md**: append-only history — per-slice build notes, test counts, and the numbered "Resolved Decisions" list.
- **docs/decisions/**: one document per *substantial* design decision (trade-offs, alternatives, spec references); the changelog entry links to it.
- **When in doubt, link instead of copy.** A pointer beats a duplicated paragraph.

The full rules, including the per-turn workflow, are in [docs/AUTONOMOUS_LOOP.md — Keeping the docs lean](docs/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).

## Current Status

Phases 0–9 are complete. Phase 8 delivered the server-side Docker foundation; Phase 9 (Real-World Deployment Preparation) is complete — all five deliverables done and grounded in the real config/client/server/test surface. **Phase 10 (Project & Test Review) is in progress** — Slices 1.1–1.7 are done: removed dead code, consolidated IRI-resolution + page-flatten + collection-IRI dedup, moved `CollectionPage` to `Iris.Core`, consolidated the parallel cache engines into `CachingReadThrough<T>`, consolidated `Accept`/`Reject` activity handlers into `FollowResponseActivityHandler<T>`, extracted shared cores for the inbox POST + community-collection endpoint handlers, completed the API surface pass (renamed `forceRefresh` → `bypassCache` across 10 Server public signatures), and **hoisted the ~9 copy-pasted per-test federation/seeding/collection-JSON helpers into `Iris.Testing`** (`TestSeeder`/`Jwk`/`JsonDoc`, ~597 lines removed, 12 new helper tests) (444→478, 34 new tests). Per-slice detail (what was built, key types, test counts, resolved decisions) lives in [docs/CHANGELOG.md](docs/CHANGELOG.md); the phased plan and open questions are in [docs/ROADMAP.md](docs/ROADMAP.md).

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
| 9 — Real-World Deployment Preparation | ✅ complete | All five deliverables done, grounded in the real config/client/server/test surface: FQDN & TLS plan + bootstrap runbook (`docs/DEPLOYMENT_PREP.md`), real-user enumeration design (`docs/ENUMERATION_DESIGN.md`), compatibility matrix (`docs/COMPATIBILITY_MATRIX.md`), test-harness extension design (`docs/INTEROP_TEST_HARNESS.md`, Decision #41), risk & gap register (`docs/RISK_GAP_REGISTER.md`); resolves OQ #2 + #3. Prep only — no live tests (Phase 13). |
| 10 — Project & Test Review | 🚧 in progress | Slices 1.1–1.7 done (444→478, 34 new tests). 1.1: dead-code sweep + IRI dedup. 1.2: CollectionPage → Core. 1.3: cache engines → CachingReadThrough. 1.4: Accept/Reject → FollowResponseActivityHandler. 1.5: inbox + community-collection → shared cores. 1.6: API surface pass (forceRefresh → bypassCache). 1.7: test-harness consolidation (per-test federation/seeding/JSON helpers → `Iris.Testing` `TestSeeder`/`Jwk`/`JsonDoc`). Remaining: smoke-test/harness bridge decision, doc sync. |
| 11 — Implementation Gaps & Usability Exploration | 📋 planned | Walk every feature end-to-end as a user; catalog gaps; expand integration tests to cover realistic journeys + error paths. |
| 12 — Spec Conformance & Missing Features | 📋 planned | Audit vs. ActivityPub/ActivityStreams specs; inventory missing features; implement high-priority gaps; conformance tests. |
| 13 — Live Federation Compatibility | ⏸ deferred | Run the public instance against real Mastodon/Lemmy/Threads; real-user enumeration. Blocked on the Phase 9 FQDN. |
| 14+ — Future | 📋 abstract | Auth upgrade, real persistence, delivery at scale, shared inbox, observability, 1.0 API review. |

**Carried forward:** (a) spec-research findings not yet captured/folded back; (b) a bare `dotnet build` in the root is blocked by MSB1011 (stray root scratch files — delete `Program.cs`/`inspect.csproj`/`packages.lock.json` manually); (c) client/server page-1 interop gap (client accepts only an `OrderedCollectionPage` first page; the server serves page 1 as an `OrderedCollection`).

Track progress in [docs/ROADMAP.md](docs/ROADMAP.md).
