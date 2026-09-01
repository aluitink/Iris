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

**Active focus: Phase 19.0b — AP-native rework (COMPLETE).** Plan: `docs/changes/161-ap-native-rework-plan.md`. **Governing principle (operator directive):** the *general flow* is AP-native (every actor/community activity flows through the actor's **outbox** — client authors → outbox POST → server delivers); *specialized* capabilities (proxy relay, search, feeds) are backed by an `iris:`-namespaced JSON-LD extension (`iris:capabilities`) for discovery but keep their own transport; mute/relay are local, non-federated, **not** AP activities (D4a). Decisions D1–D4 are **resolved** (plan §4). All sub-slices **done**: **19.0b.1** (outbox Accept/Reject + client `AcceptAsync`/`RejectAsync`), **19.0b.2a** (removed the `follows/` follow-decision endpoints + handlers; sample UI flipped to the outbox), **19.0b.3** (steps 1+2: removed the dead follow-decision client methods — change 161c; split `ILocalModerationClient` + `LocalModerationClient` off the core AP protocol layer — change 161d), **19.0b.2b** (change 161e: relocated the mute/relay **write** routes off `/ap/v1` onto a separate non-AP `MapGroup('/local/v1')`; added `mute`/`relay` `iris:capabilities` values; the client's `LocalModerationClient` now derives the local URL from the actor IRI under `/local/v1`), and **19.0b.4** (change 161f: doc sweep — swept COMPATIBILITY_MATRIX / LIVE_EVALUATION_CHECKLIST / LIVE_INTEROP_TEST_PLAN for the removed follow-decision endpoints; fixed the stale `InboxOf` doc comments in `IActivityPubClient` (every typed method now publishes to `actorId.OutboxOf()`); renamed `DeliverAsync` param `inboxId` → `targetId`).

**Phase 19.8.7 (change 161h): error & empty states** — added `catch` + user-facing error messages to the four Blazor pages that previously let exceptions propagate unhandled (Actors, Community, Instance, Feed), following the existing `Error`/`div.error` pattern used by ObjectPage/ActorDetail/Home/Compose.

**Phase 19.6.1 (change 161i): client boost/unboost** — added the client's one-call `AnnounceAsync`/`UnannounceAsync` (boost/unboost), both published to the acting actor's outbox through the signed pipeline (the server already handled `Announce` fan-out + `Undo` generically). Every management operation is now expressible as a one-call client method. Two new integration tests pin the outbox recording. Suite 1,256, 0 failed.

**Next: Phase 19.1 (live interop verification)** — 19.1.2 (F1) is unblocked: the follow Accept/Reject mechanism is now outbox-based (AP-native). Remaining 19.x items are live/UI-verification (Docker env + RayvenMX) or the deferred 19.6.5 audience-metadata.

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
