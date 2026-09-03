# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing
applications (Blazor clients, ASP.NET Core servers, or any .NET app).

This file is the **index** — it must stay small. [docs/ROADMAP.md](docs/ROADMAP.md) tracks where
we've been and what's left.

> **About this file (compacted 2026-09-03):** rebuilt index. The long running change-by-change status
> narrative (changes 161a…179, Phases 19.0b–20.7, 21.x) has been removed — that detail lives in
> [docs/changes/](docs/changes/README.md) and [docs/decisions/](docs/decisions/README.md). Only the
> active phase (Phase 22) is summarized here, with a pointer to the roadmap + the Phase 22 plan.

## Documentation

| File | Contents |
|---|---|
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns (caching, HTTP signatures, key model, proxy fallback), spec research |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details: `Iris.Core`, `Iris.Client`, `Iris.Server`, `Iris.Server.InMemory`, `Iris.Client.Extensions`, `Iris.WebCrypto` |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy, multi-instance `TestServer` harness, deferred Mastodon live test |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Working roadmap — where we've been, where we are, what's left (active phase = 22, the functional sample explorer) |
| [docs/changes/](docs/changes/README.md) | **Per-change docs**: one file per slice/change (build notes, key types, test counts, lightweight decisions) |
| [docs/decisions/](docs/decisions/README.md) | Design-decision documents (one file per substantial decision) |
| [docs/phase-notes/](docs/phase-notes/README.md) | Per-phase rationale + test-count history |
| [docs/plans/](docs/plans/) | **Phase 22 deep-dive plans** — the user-stories/component map + one document per component we build |
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
- **AP-native flow (Phase 19.0b)** — the general flow is AP-native (every actor/community activity flows through the actor's **outbox** — client authors → outbox POST → server delivers); specialized capabilities (proxy relay, search, feeds) are `iris:`-extension-discovered but keep their own transport; mute/relay are local, non-AP, on `/local/v1`.
- **Server is the object-id authority (Decision 055)** — the server mints ULIDs for authored objects; the outbox handler returns the created object in the 2xx body; the client uses the learned id.
- **C2S inbox (Decision 056)** — the inbox is a private, owner-only, per-actor collection; inbound objects stored verbatim (no id rewrite).
- **Browser-loadable media (Decision 057)** — the client's render boundary rewrites cross-origin attachment URLs to a same-origin media proxy; the wire stays verbatim.

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
- **Testing**: integration-first (xUnit + multi-instance `TestServer` harness); unit tests reserved for pure logic. The sample explorer (the primary focus) is **tested manually** (Playwright MCP) until all wanted features exist — **no new framework tests (bUnit/`dotnet test`) for UI work**; the one exception is a **specific backend change** made while developing the UI, which ships with its normal integration test (see [docs/ROADMAP.md](docs/ROADMAP.md) Phase 22 method, rule 5).

> Full conventions, including the binding 3rd-party ActivityStreams rules: [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md).

## Current Status

**Active focus: Phase 22 — the functional sample explorer.** The goal is not a beautiful UI but a more
functional explorer tool: better components, better support for viewing/reviewing activities and
objects, and better support for interacting with other servers. The work is driven by a set of **user
stories + a per-component usage map**
([docs/plans/22-sample-ui-user-stories.md](docs/plans/22-sample-ui-user-stories.md)); each item is first
**deepened into a component-level plan** under `docs/plans/22-*.md` (referenced from the roadmap's 22.0
umbrella item), then built, then **manually tested with the Playwright MCP tools** (gaps/errors resolved
before the item is closed). The high-level areas: better components (`ObjectView`, a reusable
`PagedCollection` card, a `RawInspector`, consistent card states); viewing/reviewing (object, actor,
community, instance pages + paged browsing); interacting with other servers (cross-instance navigation,
remote rendering, multi-server identity); and authoring (compose with media/markdown/sensitivity,
reply/threads). The remaining pre-Phase-22 items (the Phase 19 live-interop + raw-inspector UI halves
and the Phase 21 UI deltas) are listed in the roadmap and are largely subsumed by Phase 22.

**Latest slice (22.3, compose — 22.3 complete):** the Compose page gains the two remaining US-11
authoring capabilities on top of the existing media upload: a **Markdown** content toggle (typed
content rendered to safe HTML by the dependency-free `Markdown.ToHtml` before posting, so the stored
`content` is HTML) and a **content-sensitivity** flag + summary (the AS `sensitive` term in
`ExtensionData` + the `summary` term). A new pure helper `Iris.Core.Compose.ComposeNote.Build` (unit
tested) composes the note's wire shape, and a new `IriExtensions.IsPreRenderedHtmlContent` detector
lets `ObjectView` render pre-rendered HTML verbatim (previously it re-ran all content through the
Markdown renderer and would have shown the posted HTML as escaped literal text). Manually verified on
the docker compose FQDN stack (Markdown + sensitive note posts 202, stores `"sensitive": true` +
`summary`, and renders as real `<h1>`/`<strong>`/`<em>`/`<ul>`/`<a>` behind a reveal; plain-text
content still renders via the Markdown path). See
[docs/changes/187-22.3-compose-markdown-sensitivity.md](docs/changes/187-22.3-compose-markdown-sensitivity.md).
**22.3 is done** — next is 22.4 (cross-server polish: remote object/actor rendering via the proxy +
media proxy, US-8; multi-server identity/switching, US-2/US-24).

Status per phase, with one-line summaries, lives in [docs/ROADMAP.md](docs/ROADMAP.md); per-slice build
notes in [docs/changes/](docs/changes/README.md); substantial design calls in
[docs/decisions/](docs/decisions/README.md).

## Keeping the docs lean

This file is the **index** — it must stay small. The rules for where information belongs:

- **PLAN.md** (this file): index, conventions summary, short status. Nothing else grows over time.
- **ROADMAP.md**: phases as one-line waypoints — where we've been, where we are, what's left. No build notes, no rationale.
- **docs/changes/**: one document per slice/change (build notes, key types, test counts, lightweight decisions).
- **docs/decisions/**: one document per substantial design decision; change docs link to it.
- **docs/plans/**: Phase 22 deep-dive plans (one per component/story elaborated), referenced from the roadmap's 22.0 item.
- **When in doubt, link instead of copy.** A pointer beats a duplicated paragraph.

Full rules, including the per-turn workflow: [docs/reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](docs/reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
