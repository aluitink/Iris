# Iris — ActivityPub .NET Libraries

A set of .NET libraries for ActivityPub, designed to be embedded in existing apps and services.

**This file is the single live operating document.** It is the only thing an autonomous agent needs to read to know what's happening now and what's next; everything else in `docs/` is either a rarely-touched reference or a write-once archive. See [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) for the loop this file drives.

## Documentation

| File | Contents | Read cadence |
|---|---|---|
| **PLAN.md (this file)** | Now / Active Slice / Up Next / Inbox / Paused Questions / Recently Completed | every turn |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Append-only ledger of completed phases (one line each) | only to replenish "Up Next" or recall history |
| [docs/changes/](docs/changes/README.md) | One document per slice/change — the detailed build notes | write on completion; read rarely |
| [docs/decisions/](docs/decisions/README.md) | Substantial design decisions | write when a decision has real weight; read rarely |
| [docs/phase-notes/](docs/phase-notes/README.md) | Phase rationale and test-count notes | archival |
| [docs/plans/](docs/plans/) | Deep-dive scope docs for multi-turn workstreams (e.g. [phase-22-closeout.md](docs/plans/phase-22-closeout.md)) | read when picking up that workstream |
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns | reference |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details for Iris libraries | reference |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy | reference |
| [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md) | Binding conventions and ActivityStreams rules | before every coding turn |
| [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) | Operating instructions and doc-maintenance rules | every turn |

## Short version

- Clean, focused abstractions; no framework lock-in beyond .NET.
- One client, two directions: a single `net10.0` client used by both client apps and server-to-server flows.
- Server capability added to ASP.NET Core via `IServiceCollection` and `IApplicationBuilder` extensions.
- ActivityStreams is provided by `KristofferStrube.ActivityStreams`; Iris adds identity, signing, validation, and IRI helpers on top.
- Actor-keyed auth allows a client to fetch an actor document, then sign requests using that actor's private key.
- Community-aware by default, with Group-like actors and unified feed/collection APIs.
- API access uses versioned routes under `/ap/v1/...` and `iris:`-namespaced capabilities.
- Integration-first test model with multi-instance `TestServer` harnesses, not a sprawling unit-test suite.

## Solution layout

```text
Iris.slnx
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Client.Extensions/     net10.0 — DI/runtime integration for client apps
│   ├── Iris.Server/                net10.0 — ASP.NET Core endpoints, middleware, community feeds
│   ├── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
│   └── Iris.WebCrypto/             net10.0 — browser/WebCrypto signing support
├── tests/
│   ├── Iris.Testing/               shared multi-instance test harness
│   ├── Iris.Core.Tests/            ├── Iris.Client.Tests/            ├── Iris.Client.Extensions.Tests/
│   ├── Iris.Server.Tests/          ├── Iris.LiveInterop.Tests/       ├── SampleServer.Tests/
│   └── SampleBlazorClient.Tests/
├── samples/
│   ├── SampleServer/               minimal ASP.NET Core host
│   └── SampleBlazorClient/         sample explorer using Iris.Client
└── tools/
    └── IrisSigner/
```

## Conventions

- Target framework: `net10.0` everywhere.
- `System.Text.Json` is the serialization surface; ActivityStreams/ActivityPub objects come from the third-party package model.
- Dependency flow: `Iris.Core` -> ActivityStreams + BCL; `Iris.Client` depends on `Iris.Core`; `Iris.Server` depends on `Iris.Core` + `Iris.Client` + ASP.NET Core.
- Caching is explicit: every read path that is cached exposes a `bypassCache` escape hatch.
- Versioned endpoints and `iris:` capability terms are authoritative.
- Testing is integration-first and browser-assisted where UI behavior matters.

## Now

**Phase 23 — broader federation maturity & live-account remediation.** The locally-actionable follow-on to the Phase 22 explorer rebuild (per [phase-22-closeout.md §5](docs/plans/phase-22-closeout.md#5-future-follow-on-work)): C2S cache invariants, Mastodon wire compatibility, and cross-instance moderation / community / follow propagation — all testable in-process on the `TestServer` / `FederationTopology` harnesses. Only the two live-infrastructure items (a public FQDN, real external Mastodon accounts) are deferred.

## Active Slice

None in progress — the next turn picks the top item from **Up Next** below.

<!-- When a slice is in flight, replace the line above with:
- **Slice:** <title>
- **Definition of done:** <the specific checklist for *this* slice — build clean, tests green, plus anything slice-specific>
- **Remaining steps:** <what's left, if resumed mid-slice>
-->

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. When this drops below ~3 items, replenish it from [docs/plans/phase-22-closeout.md](docs/plans/phase-22-closeout.md) or by expanding the next phase in [docs/ROADMAP.md](docs/ROADMAP.md).

> **Phase 23 status (22.11):** Phase 22's *locally-actionable* scope is fully closed (22.1–22.10). Phase 23
> — **broader federation maturity & live-account remediation** (per [phase-22-closeout.md §5](docs/plans/phase-22-closeout.md#5-future-follow-on-work)) — is now the active phase. It opens with locally-testable
> hardening (C2S cache invariants, Mastodon wire compatibility, cross-instance propagation on the in-process
> `FederationTopology`), deferring only the two live-infrastructure items (a public FQDN, real external
> Mastodon accounts) that this environment cannot provision.

1. **22.12 — Mastodon `Digest` header casing (draft-10 wire form):** an inbound request carrying the uppercase `Digest: SHA-256=…` header (what a strict Mastodon / draft-10 peer emits) must still verify. Add a test-first integration test; normalize the digest algorithm token at exactly one boundary (signer + verifier) if it fails. (Locally testable — single-instance `TestServer`.)
2. **22.13 — cross-instance moderation undo propagation:** on a 2-instance `FederationTopology`, A blocks/flags B, then A publishes `Undo(Block)`/`Undo(Flag)`; assert B's instance receives the `Undo` and removes the edge it recorded (the "moderation propagation" theme end-to-end). (In-process loopback — no external infra.)
3. **22.14 — cross-instance community un-follow propagation:** on a 2-instance `FederationTopology`, community C on A follows an actor on B, then C publishes `Undo(Follow)`; assert B receives it and its `followers` no longer lists C (the "community/follow hardening" theme; folds in the community `following`/`followers` page invalidation). (In-process loopback — no external infra.)
4. External-FQDN verification — resolver reachability, public navigation, browser verification over live hostnames. (Needs a reachable public FQDN / reverse proxy — not available in this environment; deferred.) See [phase-22-closeout.md §1](docs/plans/phase-22-closeout.md#1-external-fqdn-verification).
5. Live federation/interop checks against public Mastodon accounts (follow, post/receive, signatures, pagination, community flows). (Needs real external accounts; deferred.) See [phase-22-closeout.md §2](docs/plans/phase-22-closeout.md#2-live-federation-and-interop-checks).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(none currently)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

- 22.11: **Phase 23 opens** — fixed the moderation-collection cache-invalidation gap (C2S invariant): `blocks`/`flags`/`mutes` pages were served through the 60s-TTL page cache but never invalidated on a local moderation write (a `Block`/`Flag`/mute record or its `Undo`/un-mute), so the owner's card stayed stale until TTL; generalized `InvalidateLocalOutboxPage` → `InvalidateLocalCollectionPage` and wired it into the outbox-publish `Block`/`Flag`/`Undo` branches + both mute handlers. New `ModerationCollectionCacheInvalidationIntegrationTests` (5), prime→write→plain-read (proven to fail on the old code). → [docs/changes/229-22.11-moderation-collection-cache-invalidation.md](docs/changes/229-22.11-moderation-collection-cache-invalidation.md)
- 22.10: fixed the local no-Docker CORS origin gap (finding B) — widened `SampleServer`'s default `Iris__CorsOrigins` to `http://localhost:8090,http://localhost:8080` so both documented local paths work out of the box; new `SampleServerCorsTests` (5) host the real server and assert the CORS header on the wire (proven to fail on the old default). → [docs/changes/228-22.10-fix-local-cors-origin-gap.md](docs/changes/228-22.10-fix-local-cors-origin-gap.md)
- 22.9: fixed the WASM browser login serialization regression (finding A) — the `Iris.WebCrypto` bootstrap passed `CancellationToken` as a JSON interop arg (misdiagnosed as a `Task`); reordered to the dedicated `InvokeAsync` overload + new `Iris.WebCrypto.Tests` (first coverage of the browser-only path via a `FakeJsRuntime` that JSON-serializes every arg; proven to fail on the old code). → [docs/changes/227-22.9-fix-wasm-webcrypto-login-serialization.md](docs/changes/227-22.9-fix-wasm-webcrypto-login-serialization.md)
- 22.8: final manual UI verification pass — wire shape + server endpoints verified clean, but found **A** (hard regression: WASM browser login throws a `Task`-serialization error in the `Iris.WebCrypto` key-import path — untested, breaks the documented browser log-on) and **B** (the local no-Docker README path CORS-blocks because the server's default `Iris__CorsOrigins` lacks `http://localhost:8080`). Findings recorded; both fixed (22.9 + 22.10). → [docs/changes/226-22.8-ui-verification-findings.md](docs/changes/226-22.8-ui-verification-findings.md)

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
