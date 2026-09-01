# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing applications (Blazor clients, ASP.NET Core servers, or any .NET app).

This file is the **index** — it must stay small. [docs/ROADMAP.md](docs/ROADMAP.md) tracks where we've been and what's left.

## Documentation

| File | Contents |
|---|---|
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns (caching, HTTP signatures, key model, proxy fallback), spec research |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details: `Iris.Core`, `Iris.Client`, `Iris.Server`, `Iris.Server.InMemory`, `Iris.Client.Extensions`, `Iris.WebCrypto` |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy, multi-instance `TestServer` harness, deferred Mastodon live test |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Working roadmap — where we've been, where we are, what's left (Phase 19 = live manual integration-testing program) |
| [docs/changes/](docs/changes/README.md) | **Per-change docs**: one file per slice/change (build notes, key types, test counts, lightweight decisions) |
| [docs/decisions/](docs/decisions/README.md) | Design-decision documents (one file per substantial decision) |
| [docs/phase-notes/](docs/phase-notes/README.md) | Per-phase rationale + test-count history |
| [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md) | **Binding** coding conventions, incl. 3rd-party `KristofferStrube.ActivityStreams` rules |
| [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) | Operating instructions for the autonomous dev loop + doc-maintenance rules |

## The Short Version

- **Clean and simple** — small, focused abstractions; no framework lock-in beyond .NET itself.
- **One client, two directions** — a single `net10.0` client library used by both client apps (Blazor) and servers (server-to-server).
- **Server as extension** — ActivityPub server capability added to an existing ASP.NET Core app via `IServiceCollection`/`IApplicationBuilder` extensions.
- **ActivityStreams via `KristofferStrube.ActivityStreams`** — the NuGet package provides the object model + JSON-LD; `Iris.Core` adds Iris-specific concerns (signing, identity, IRI helpers, validation) on top, never re-implements it.
- **Actor-keyed client auth** — client authenticates (Basic in v1; OAuth2 available), fetches the actor document with the private key, signs subsequent requests with it.
- **Layered caching** — short-TTL caches on client and server; every cached read has a `bypassCache` escape hatch.
- **Community-aware** — first-class `Group` actors (Lemmy-style communities) with unified feed/collection APIs.
- **Versioned API surface** — route-prefix versioning (`/ap/v1/...`); `Iris-Version` meta header; `iris:capabilities` for discovery.
- **Integration-first testing** — end-to-end tests against multiple in-process server instances, not a sprawl of unit tests.

## Solution Layout

```
Iris.slnx
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Client.Extensions/     net10.0 — DI/extensions for client app integration
│   ├── Iris.Server/                net10.0 — ASP.NET Core extensions, endpoints, middleware, community feeds
│   ├── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
│   └── Iris.WebCrypto/             net10.0 — WebCrypto browser signing support
├── tests/
│   ├── Iris.Testing/               shared multi-instance TestServer harness
│   ├── Iris.Core.Tests/            ├── Iris.Client.Tests/            ├── Iris.Client.Extensions.Tests/
│   ├── Iris.Server.Tests/          ├── Iris.LiveInterop.Tests/       ├── SampleServer.Tests/
│   └── SampleBlazorClient.Tests/
└── samples/
    ├── SampleServer/               minimal ASP.NET Core app hosting Iris.Server
    └── SampleBlazorClient/         Blazor WebAssembly server-explorer using Iris.Client
```

## Conventions (summary)

- **TFM: net10.0** everywhere. C# latest, nullable enabled, file-scoped namespaces.
- **`System.Text.Json` exclusively**; ActivityStream/ActivityPub types come from `KristofferStrube.ActivityStreams` — not re-implemented.
- **Central package management** (`Directory.Packages.props`).
- **Dependency direction**: `Iris.Core` → ActivityStreams + BCL; `Iris.Client` → `Iris.Core`; `Iris.Server` → `Iris.Core` + `Iris.Client` + ASP.NET Core; `Iris.Server.InMemory` → `Iris.Server`.
- **Caching**: every cached read exposes `bypassCache`. No cached path is opaque.
- **Versioning**: route prefix (`/ap/v1/...`) is authoritative; new capabilities via `iris:`-namespaced terms.
- **Testing**: integration-first (xUnit + multi-instance `TestServer` harness); unit tests reserved for pure logic.

> Full conventions, including the binding 3rd-party ActivityStreams rules: [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md).

## Current Status

Phases -1 through 12, 15, 16, 17, and 18 are complete, as are the Sample Explorer enhancement rounds (all rounds live-browser-verified). **Phase 19.0 (evaluation environment) is complete**. **Phase 19.1.1 (Iris↔Iris baseline) is complete**: F-1911-1 and F-1911-2 **fixed** (commit 262fd09); F-1911-3 root cause confirmed (community signing identity not registered). **Phase 19.1.2 (follow scenarios) in progress**: F2 (we follow RayvenMX) **PASS (signature)** — the F-1912-1 signature fix made Mastodon accept our Follow (202); F-1911-3 (community signing identity not registered) **fixed** (server + client) and verified (regression test + community-signed follow to Mastodon 202 via IrisSigner). RayvenMX's `Accept` still pending (their side to process). F1/F3/F4 not tested (require RayvenMX's action). **Phase 19.3.1/19.3.2 (two-instance loop/echo safety) is complete** (commit 262616e — inbox add-if-absent re-delivery guard); **Phase 19.3.3 (Announce propagation) is complete** (commit a0c86ad — propagated-boost IRI keyed to the announcer; local followers recorded in their outbox directly). **Phase 19.3.4 (Delete propagation, both directions) is complete** (direction 2 — a remote actor deletes a note we hold a copy of — now covered by a two-instance test proving the `DeleteActivityHandler` owner guard, correct scope, and no re-propagation; change 144). **Phase 19.3.5 (Follow-edge convergence) is complete** (a two-instance Follow/Undo/Follow cycle over the wire converges both sides' `following`/`followers` to the single edge — no orphan, no duplicate, stable public collections; the `UndoActivityHandler` already removed the edge on both sides, so no production change; change 145). **Phase 19.3.6 (Update propagation) is complete** (direction 2 — a remote author edits a note we hold a copy of — now covered by a two-instance test proving the `UpdateActivityHandler` owner guard, correct scope, and no re-propagation; direction 1 was already covered; no production change; change 146). **Phase 19.3.7 (Recreation stability) is complete** (a recreated host's un-truncated file-backed delivery journal replays the already-delivered Create over the wire — a genuine re-transmission, not a no-op that never left — and the peer's `InboxProcessor` add-if-absent-by-Id gate stores it as a no-op: the note is stored exactly once, the recipient's outbox is unchanged in length (no duplicate edge), and there is no re-fan-out storm; no production change — the guarantee rests on the at-least-once journal replay + the inbox-Id guard, pinned end-to-end by `Recreation_DeliveredCreateReplayed_StoredOnce_NoReFanOut_OutboxUnchanged`; change 147). **Phase 19.5.1 (community creation surface) — community READ surface complete** (change 148): the community document, `members`, `feed`, `following`/`followers`, and search collections were already served; the missing piece was the advertised **`outbox` link** — `GET /ap/v1/c/{name}/outbox` (the READ counterpart of the `POST` community outbox publish endpoint) is now a paged collection served through the local collection-page cache, so a remote client resolving the community's outbox link finds the community's authored activities instead of a 404; 5 new integration tests, full suite 1,204 green. Still open for full 19.5.1: the UI creation *write* path + WebFinger/`iris:capabilities` discovery (live/UI verification). **Phase 19.5.5 (community feed correctness) — newest-first merge fixed** (change 149): the community feed was documented "newest first" but actually concatenated the members' outboxes in member-IRI order (grouped by member), so a member's newest post did not rank above another member's older post; `ICommunityFeedService` now merges by (outbox position, then member IRI) — a stable, deterministic newest-first merge — keeping the IRI de-duplication and `?q` content filter; new `CommunityFeedCorrectnessIntegrationTests` + updated `CommunityFeedIntegrationTests`/`CommunitySearchIntegrationTests` order assertions, full suite 1,208 green. Still open for full 19.5.5: the remote-content half + `?refresh=true` bypass (live/UI verification). Next: continue 19.5.x community slices (membership management, peers, moderation) and 19.1.3–19.1.8 live verification (and 19.4 remediation triage).

- **Blocked (external)** — Phase 13.5–13.10 live interop and Phase 14 remediation are folded into Phase 19.1 (live interop verification) + 19.4 (remediation); the CI-testable sub-slices and the CI-gating model are already done.
- **Tabled** — external/remote community-style interaction testing (per operator decision).

Status per phase, with one-line summaries, lives in [docs/ROADMAP.md](docs/ROADMAP.md); per-slice build notes in [docs/changes/](docs/changes/README.md); substantial design calls in [docs/decisions/](docs/decisions/README.md).

## Keeping the docs lean

This file is the **index** — it must stay small. The rules for where information belongs:

- **PLAN.md** (this file): index, conventions summary, short status. Nothing else grows over time.
- **ROADMAP.md**: phases as one-line waypoints — where we've been, where we are, what's left. No build notes, no rationale.
- **docs/changes/**: one document per slice/change (build notes, key types, test counts, lightweight decisions).
- **docs/decisions/**: one document per substantial design decision; change docs link to it.
- **When in doubt, link instead of copy.** A pointer beats a duplicated paragraph.

Full rules, including the per-turn workflow: [docs/reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](docs/reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
